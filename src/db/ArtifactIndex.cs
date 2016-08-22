using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class ArtifactIndex
    {
        [Key]
        [StringLength(450)]
        public string Index { get; set; }

        [Required]
        [Index]
        [StringLength(40)]
        public string Hash { get; set; }

        [ForeignKey("Hash")]
        public virtual Artifact Artifact { get; set; }
    }
}
