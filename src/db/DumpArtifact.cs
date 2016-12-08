using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    [DataContract]
    public class DumpArtifact
    {
        [Key]
        [Column(Order = 0)]
        [DataMember]
        public string DumpId { get; set; }

        [Key]
        [Column(Order = 1)]
        [StringLength(450)]
        [DataMember]
        public string LocalPath { get; set; }

        [NotMapped]
        [DataMember]
        public string RelativePath { get { return LocalPath.Replace(":", string.Empty).Replace('\\', '/').TrimStart('.').TrimStart('/'); } }

        [DataMember]
        public string Hash { get; set; }
        
        [StringLength(450)]
        [DataMember]
        public string Index { get; set; }

        [DataMember]
        public bool DebugCritical { get; set; }

        [DataMember]
        public bool ExecutableImage { get; set; }

        [Timestamp]
        public byte[] Timestamp { get; set; }


        [ForeignKey("DumpId")]
        public virtual Dump Dump { get; set; }
        
        [ForeignKey("Hash")]
        public virtual Artifact Artifact { get; set; }


    }
}
