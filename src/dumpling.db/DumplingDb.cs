using System;
using System.Collections.Generic;
using System.Data.Entity;
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

        public async Task AddArtifactAsync(Artifact artifact)
        {
            this.Artifacts.Add(artifact);

            await this.SaveChangesAsync();

            await this.Entry(artifact).ReloadAsync();

            await UpdateIncompleteDumpArtifacts(artifact);
        }

        private async Task UpdateIncompleteDumpArtifacts(Artifact artifact)
        {
            foreach (var index in artifact.Indexes)
            {
                await this.Database.ExecuteSqlCommandAsync(DUMPARTIFACT_UPDATE_QUERY, artifact.Hash, index);
            }
        }

        private const string DUMPARTIFACT_UPDATE_QUERY = "UPDATE [DumpArtifacts] SET [Hash] = @p0 WHERE [Index] = @p1";
    }
}
