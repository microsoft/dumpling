// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public class Artifact
    {
        public Artifact()
        {
            this.Dumps = new HashSet<Dump>();
        }

        [Key]
        public string Index { get; set; }

        [Required]
        public string Format { get; set; }

        public bool Available { get; set; }

        public DateTime CreatedTime { get; set; }
        
        public virtual ICollection<Dump> Dumps { get; set; }

        [Timestamp]
        public byte[] Timestamp { get; set; }
    }
}
