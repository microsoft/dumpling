using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class ManifestEntry
    {
        [Key]
        public int DumpId { get; set; }
        
        [Key]
        public string Index { get; set; }

        public string LocalPath { get; set; }

        [ForeignKey("DumpId")]
        public virtual Dump Dump { get; set; }

        [ForeignKey("Index")]
        public virtual Artifact Artifact { get; set; }

    }
}
