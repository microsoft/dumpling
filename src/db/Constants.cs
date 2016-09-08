using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace dumpling.db
{
    public static class OS
    {
        public const string Windows = "windows";
        public const string Linux = "linux";
        public const string Mac = "darwin";
        public const string Unknown = "unknown";
    }

    public static class ArtifactFormat
    {
        public const string ElfCore = "elfcore";
        public const string Minidump = "minidump";
        public const string MachCore = "machcore";
        public const string Elf = "elf";
        public const string PE = "pe";
        public const string PDB = "pdb";
        public const string MachO = "macho";
        public const string Unknown = "unknown";
    }

    public static class IndexPrefix
    {
        public const string Elf = "elf-buildid-";
        public const string SHA1 = "sha1-";
        public const string PDB = "";
        public const string PE = "";
        public const string Mach = "mach-uuid-";
    }
}
