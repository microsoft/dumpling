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
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class Dump
    {
        public Dump()
        {
            this.Artifacts = new HashSet<Artifact>();

            this.Properties = new HashSet<Property>();
        }

        public int DumpId { get; set; }

        public string DisplayName { get; set; }
        
        public string Origin { get; set; }

        public string FailureHash { get; set; }

        [Required]
        [Index]
        public DateTime DumpTime { get; set; }

        public virtual ICollection<Artifact> Artifacts { get; set; }

        public virtual ICollection<Property> Properties { get; set; }

        [ForeignKey("FailureHash")]
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
