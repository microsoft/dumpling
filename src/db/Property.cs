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
    public class Property
    {
        public int PropertyId { get; set; }

        [Required]
        [Index]
        [StringLength(450)]
        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public string Value { get; set; }

        [Required]
        [Index]
        [StringLength(40)]
        public string DumpId { get; set; }

        [ForeignKey("DumpId")]
        public virtual Dump Dump { get; set; }
    }
}
