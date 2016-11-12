using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
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

        public async Task<DumpArtifact> GetOrAddAsync(DumpArtifact dumpArtifact)
        {
            return await this.GetOrAddAsync(this.DumpArtifacts, dumpArtifact, dumpArtifact.DumpId, dumpArtifact.LocalPath);
        }

        private async Task<T> GetOrAddAsync<T>(DbSet<T> dbSet, T entity, params object[] keys)
            where T : class
        {
            if (this.ChangeTracker.HasChanges())
            {
                throw new InvalidOperationException("Pending changes detected in the current context.  GetOrAdd makes transactional changes to the context and database and can only be called on a context with no pending changes.");
            }

            //try to get find the value first
            T ret = await dbSet.FindAsync(keys);

            if(ret == null)
            {
                try
                {
                    dbSet.Add(entity);

                    await this.SaveChangesAsync();

                    ret = entity;
                }
                catch (DbUpdateException e) when (IsUniqueViolationException(e))
                {
                    dbSet.Remove(entity);

                    ret = await dbSet.FindAsync(keys);
                }
            }

            await this.Entry<T>(ret).GetDatabaseValuesAsync();

            return ret;
        }

        private bool IsUniqueViolationException(DbUpdateException e)
        {
            var sqlEx = e.InnerException as SqlException;


            return sqlEx != null && sqlEx.Errors.OfType<SqlError>().Any(se => se.Number == 2601 || se.Number == 2627 /* PK/UKC violation */);
        }

        public async Task AddArtifactAsync(Artifact artifact)
        {
            this.Artifacts.Add(artifact);

            await this.SaveChangesAsync();

            await this.Entry(artifact).ReloadAsync();

            await UpdateIncompleteDumpArtifacts(artifact);
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
    }
}
