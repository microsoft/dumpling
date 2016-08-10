using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class IndexedArtifact
    {
        public IndexedArtifact()
        {
            this.Dumps = new HashSet<Dump>();
        }

        [Key]
        public string Index { get; set; }

        public string Hash { get; set; }

        public virtual ICollection<Dump> Dumps { get; set; }

        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
