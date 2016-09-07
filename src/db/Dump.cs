// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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
    public class Dump
    {
        public Dump()
        {
            this.DumpArtifacts = new HashSet<DumpArtifact>();

            this.Properties = new HashSet<Property>();
        }

        [Key]
        [StringLength(40)]
        [DataMember]
        public string DumpId { get; set; }

        [DataMember]
        public string DisplayName { get; set; }

        [DataMember]
        [StringLength(128)]
        public string User { get; set; }

        [DataMember]
        [StringLength(32)]
        [Required]
        public string Os { get; set; }
        
        public string FailureHash { get; set; }

        [Required]
        [Index]
        [DataMember]
        public DateTime DumpTime { get; set; }

        [DataMember]
        public virtual ICollection<DumpArtifact> DumpArtifacts { get; set; }

        [DataMember]
        public virtual ICollection<Property> Properties { get; set; }

        [ForeignKey("FailureHash")]
        [DataMember]
        public virtual Failure Failure { get; set; }

        [Timestamp]
        public byte[] Timestamp { get; set; }

        public IDictionary<string, string> GetPropertyBag()
        {
            var propBag = new Dictionary<string, string>();

            foreach(var p in Properties)
            {
                propBag[p.Name] = p.Value;
            }

            return propBag;
        }
    }
}
