using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Core;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class DumplingDb : DbContext
    {
        public DbSet<Artifact> Artifacts { get; set; }
        
        public DbSet<ArtifactIndex> ArtifactIndexes { get; set; }

        public DbSet<Dump> Dumps { get; set; }

        public DbSet<DumpArtifact> DumpArtifacts { get; set; }

        public DbSet<Failure> Failures { get; set; }
        
        public DbSet<Property> Properties { get; set; }

        public async Task<bool> TryAddAsync(DumpArtifact dumpArtifact, CancellationToken cancelToken)
        {
            return await this.GetOrAddAsync(dumpArtifact, cancelToken) == dumpArtifact;
        }

        public async Task<DumpArtifact> GetOrAddAsync(DumpArtifact dumpArtifact, CancellationToken cancelToken)
        {
            return await this.GetOrAddAsync(cancelToken, this.DumpArtifacts, dumpArtifact, dumpArtifact.DumpId, dumpArtifact.LocalPath);
        }

        public async Task<bool> TryAddAsync(Artifact artifact, CancellationToken cancelToken)
        {
            return await this.GetOrAddAsync(artifact, cancelToken) == artifact;
        }

        public async Task<Artifact> GetOrAddAsync(Artifact artifact, CancellationToken cancelToken)
        {
            return await this.GetOrAddAsync(cancelToken, this.Artifacts, artifact, artifact.Hash);
        }

        private async Task<T> GetOrAddAsync<T>(CancellationToken cancelToken, DbSet<T> dbSet, T entity, params object[] keys)
            where T : class
        {
            if (this.ChangeTracker.HasChanges())
            {
                throw new InvalidOperationException("Pending changes detected in the current context.  GetOrAdd makes transactional changes to the context and database and can only be called on a context with no pending changes.");
            }

            //try to get find the value first
            T ret = await dbSet.FindAsync(cancelToken, keys);

            if(ret == null)
            {
                try
                {
                    dbSet.Add(entity);

                    await this.SaveChangesAsync(cancelToken);

                    ret = entity;
                }
                catch (DbUpdateException e) when (IsUniqueViolationException(e))
                {
                    dbSet.Remove(entity);

                    ret = await dbSet.FindAsync(cancelToken, keys);
                }
            }

            await this.Entry<T>(ret).ReloadAsync(cancelToken);

            return ret;
        }

        private bool IsUniqueViolationException(DbUpdateException e)
        {
            var ex = (Exception)(e.InnerException as UpdateException) ?? (Exception)e;

            var sqlEx = e.InnerException as SqlException;
            
            return sqlEx != null && sqlEx.Errors.OfType<SqlError>().Any(se => se.Number == 2601 || se.Number == 2627 /* PK/UKC violation */);
        }

        public async Task<bool> AddArtifactAsync(Artifact artifact, CancellationToken cancelToken)
        {
            if (await this.TryAddAsync(artifact, cancelToken))
            {
                await UpdateIncompleteDumpArtifacts(artifact);

                return true;
            }

            return false;
        }

        public async Task<bool> DeleteArtifactAsync(string hash)
        {
            int recordsAffected = await this.Database.ExecuteSqlCommandAsync(DUMPARTIFACT_UNLINK_UPDATE_QUERY, hash);

            recordsAffected += await this.Database.ExecuteSqlCommandAsync(ARTIFACTINDEX_DELETE_QUERY, hash);

            recordsAffected += await this.Database.ExecuteSqlCommandAsync(ARTIFACT_DELETE_QUERY, hash);

            return recordsAffected > 0;
        } 

        public IEnumerable<Dump> FindDumps(DateTime startTime, DateTime endTime, Dictionary<string, string> propertyDictionary)
        {
            StringBuilder query = new StringBuilder("SELECT [D].* FROM [DUMPS] AS [D] ");

            string failureHash = null;

            int pi = 0;

            List<object> queryParameters = new List<object>();

            if (propertyDictionary != null && propertyDictionary.Count != 0)
            {
                if(propertyDictionary.ContainsKey(Failure.FAILURE_HASH_PROP_KEY))
                {
                    failureHash = propertyDictionary[Failure.FAILURE_HASH_PROP_KEY];

                    propertyDictionary.Remove(Failure.FAILURE_HASH_PROP_KEY);
                }

                var properties = new List<KeyValuePair<string, string>>(propertyDictionary);

                for (int i = 0; i < properties.Count; i++)
                {
                    var prop = properties[i];

                    query.Append($"JOIN [Properties] [P{i}] ON [D].[DumpId] = [P{i}].[DumpId] AND [P{i}].[Name] = @p{pi++} AND [P{i}].[Value] = @p{pi++} ");

                    queryParameters.Add(prop.Key);

                    queryParameters.Add(prop.Value);
                }
            }

            query.Append("WHERE ");

            if (failureHash != null)
            {
                if (failureHash == "UNTRIAGED")
                {
                    query.Append($"[D].[FailureHash] IS NULL AND ");
                }
                else
                {
                    query.Append($"[D].[FailureHash] = @p{pi++} AND ");

                    queryParameters.Add(failureHash);
                }
            }

            query.Append($"[D].[DumpTime] >= @p{pi++} AND [D].[DumpTime] <= @p{pi++}");

            queryParameters.Add(startTime);

            queryParameters.Add(endTime);

            var dumps = this.Dumps.SqlQuery(query.ToString(), queryParameters.ToArray()).AsQueryable<Dump>().Include(d => d.Properties).Include(d => d.Failure);

            return dumps.ToArray();
        }

        private async Task UpdateIncompleteDumpArtifacts(Artifact artifact)
        {
            foreach (var index in artifact.Indexes)
            {
                await this.Database.ExecuteSqlCommandAsync(DUMPARTIFACT_UPDATE_QUERY, artifact.Hash, index.Index);
            }
        }
        
        private const string DUMPARTIFACT_UPDATE_QUERY = "UPDATE [DumpArtifacts] SET [Hash] = @p0 WHERE [Index] = @p1";

        private const string DUMPARTIFACT_UNLINK_UPDATE_QUERY = "UPDATE [DumpArtifacts] SET [Hash] = NULL WHERE [Hash] = @p0";
        private const string ARTIFACTINDEX_DELETE_QUERY = "DELETE FROM [ArtifactIndexes] WHERE [Hash] = @p0";
        private const string ARTIFACT_DELETE_QUERY = "DELETE FROM [Artifacts] WHERE [Hash] = @p0";
    }
}
