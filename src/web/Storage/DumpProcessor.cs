using dumpling.db;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web;
using System.Threading.Tasks;
using FileFormats.ELF;
using FileFormats;
using FileFormats.Minidump;
using FileFormats.MachO;
using System.Data.Entity.Migrations;
using dumpling.web.telemetry;

namespace dumpling.web.Storage
{
    public class DumpProcessor : ArtifactProcessor
    {
        private object _fileFormatReader;

        public DumpProcessor(string optoken, string localRoot, string path, string expectedHash, string dumpId, string localPath) 
            : base(optoken, localRoot, path, expectedHash, dumpId, localPath, true)
        {

        }

        public string DumpOS { get; private set; }

        protected override void ProcessDecompressedFile(Stream decompressed)
        {
            if (IsELFCore(decompressed))
            {
                Format = ArtifactFormat.ElfCore;
                DumpOS = OS.Linux;
            }
            else if (IsMinidump(decompressed))
            {
                Format = ArtifactFormat.Minidump;
                DumpOS = OS.Windows;
            }
            else if (IsMachCore(decompressed))
            {
                Format = ArtifactFormat.MachCore;
                DumpOS = OS.Mac;
            }
            else
            {
                Format = ArtifactFormat.Unknown;
                DumpOS = OS.Unknown;
            }
            Uuid = Hash;
            Index = BuildIndexFromModuleUUID(Hash, IndexPrefix.SHA1, FileName);
        }

        protected override async Task StoreArtifactAsync()
        {
            try
            {
                await base.StoreArtifactAsync();

                //update the dump properties
                var dump = await _dumplingDb.Dumps.FindAsync(DumpId);

                dump.Os = DumpOS;

                await _dumplingDb.SaveChangesAsync();

                //store the dump artifacts for the loaded modules as well
                await StoreLoadedModulesAsync();
            }
            catch (Exception e) when (Telemetry.TrackExceptionFilter(e)) { }
        }

        private async Task StoreLoadedModulesAsync()
        {
            try
            {
                IList<DumpArtifact> loadedModules;

                switch (this.Format)
                {
                    case "elfcore":
                        loadedModules = ReadELFCoreLoadedModules(DumpId);
                        break;
                    default:
                        loadedModules = new DumpArtifact[] { };
                        break;
                }

                foreach (var dumpArt in loadedModules)
                {
                    _dumplingDb.DumpArtifacts.AddOrUpdate(dumpArt);

                    await _dumplingDb.SaveChangesAsync();
                }
            }
            catch (Exception e) when (Telemetry.TrackExceptionFilter(e)) { }
        }

        private IList<DumpArtifact> ReadELFCoreLoadedModules(string dumpId)
        {
            try
            {
                var coreFile = _fileFormatReader as ELFCoreFile;

                //get this property so that it will force the validation of the file format before we allocate anything else
                var fileTable = coreFile.FileTable;

                var dumpArtifacts = new List<DumpArtifact>();

                foreach (var image in coreFile.LoadedImages)
                {
                    string index = null;
                    string uuid = null;
                    bool executableImage = false;
                    try
                    {
                        executableImage = image.Image.Header.Type == ELFHeaderType.Executable;

                        //this call will throw an exception if the loaded image doesn't have a build id.  
                        //Unfortunately there is no way to check if build id exists without ex
                        var buildId = image.Image.BuildID;

                        if (buildId != null)
                        {
                            uuid = string.Concat(buildId.Select(b => b.ToString("x2"))).ToLowerInvariant();

                            index = BuildIndexFromModuleUUID(uuid, IndexPrefix.Elf, Path.GetFileName(image.Path));
                        }
                    }
                    catch { }

                    dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = image.Path, Index = index, DebugCritical = true, ExecutableImage = executableImage });

                    //if the image is libcoreclr.so also add libmscordaccore.so and libsos.so at the same path
                    if (Path.GetFileName(image.Path) == "libcoreclr.so")
                    {
                        var localDir = Path.GetDirectoryName(image.Path);

                        //currently the dac index and the sos index are not imbedded in libcoreclr.so 
                        //this should eventually be the case, but for now set the indexes using the buildid from libcoreclr.so
                        //and we will manually add these indexes to the atifact store
                        dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = Path.Combine(localDir, "libmscordaccore.so").Replace('\\', '/'), Index = BuildIndexFromModuleUUID(uuid, IndexPrefix.Elf, "libmscordaccore.so"), DebugCritical = true });

                        dumpArtifacts.Add(new DumpArtifact() { DumpId = dumpId, LocalPath = Path.Combine(localDir, "libsos.so").Replace('\\', '/'), Index = BuildIndexFromModuleUUID(uuid, IndexPrefix.Elf, "libsos.so"), DebugCritical = true });
                    }
                }

                return dumpArtifacts;
            }
            catch
            {
                return new DumpArtifact[] { };
            }
        }

        private bool IsELFCore(Stream decompressed)
        {
            try
            {
                var coreFile = new ELFCoreFile(new StreamAddressSpace(decompressed));

                //get this property so that it will force the validation of the file format before we allocate anything else
                var fileTable = coreFile.FileTable;

                _fileFormatReader = coreFile;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool IsMinidump(Stream decompressed)
        {
            try
            {
                return Minidump.IsValidMinidump(new StreamAddressSpace(decompressed));
            }
            catch
            {
                return false;
            }
        }

        private bool IsMachCore(Stream decompressed)
        {
            try
            {
                var machCore = new MachCore(new StreamAddressSpace(decompressed));

                return machCore.IsValidCoreFile;
            }
            catch
            {
                return false;
            }
        }
    }
}