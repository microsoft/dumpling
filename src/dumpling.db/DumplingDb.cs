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

        public DbSet<Dump> Dumps { get; set; }

        public DbSet<Failure> Failures { get; set; }
        
        public DbSet<Property> Properties { get; set; }
    }
}
