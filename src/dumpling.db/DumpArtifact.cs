using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class DumpArtifact
    {
        [Key]
        [Column(Order = 0)]
        public int DumpId { get; set; }

        [Key]
        [Column(Order = 1)]
        [StringLength(450)]
        public string LocalPath { get; set; }

        public string Hash { get; set; }
        
        [StringLength(450)]
        public string Index { get; set; }

        [Timestamp]
        public byte[] Timestamp { get; set; }

        [ForeignKey("DumpId")]
        public virtual Dump Dump { get; set; }
        
        [ForeignKey("Hash")]
        public virtual Artifact Artifact { get; set; }


    }
}
