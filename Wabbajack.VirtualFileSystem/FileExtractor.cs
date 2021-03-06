﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using Compression.BSA;
using OMODFramework;
using Wabbajack.Common.StatusFeed;
using Wabbajack.Common.StatusFeed.Errors;
using Wabbajack.Common;
using Wabbajack.Common.FileSignatures;
using Utils = Wabbajack.Common.Utils;


namespace Wabbajack.VirtualFileSystem
{
    public class FileExtractor
    {

        private static SignatureChecker archiveSigs = new SignatureChecker(Definitions.FileType.TES3, 
            Definitions.FileType.BSA,
            Definitions.FileType.BA2,
            Definitions.FileType.ZIP,
            Definitions.FileType.EXE,
            Definitions.FileType.RAR,
            Definitions.FileType._7Z);
        
        public static async Task<ExtractedFiles> ExtractAll(WorkQueue queue, AbsolutePath source, IEnumerable<RelativePath> OnlyFiles = null, bool throwOnError = true)
        {
            try
            {
                var sig = await archiveSigs.MatchesAsync(source);
                
                if (source.Extension == Consts.OMOD)
                    return await ExtractAllWithOMOD(source);

                Utils.Log($"Extracting {sig}");
                
                switch (sig)
                {
                    case Definitions.FileType.BSA:
                    case Definitions.FileType.TES3:
                    case Definitions.FileType.BA2:
                        return await ExtractAllWithBSA(queue, source);
                    case Definitions.FileType.EXE:
                        return await ExtractAllExe(source);
                    case Definitions.FileType._7Z:
                    case Definitions.FileType.ZIP:
                    case Definitions.FileType.RAR:
                        return await ExtractAllWith7Zip(source, OnlyFiles);
                }
                throw new Exception("Invalid archive format");
            }
            catch (Exception ex)
            {
                if (!throwOnError)
                    return new ExtractedFiles(await TempFolder.Create());

                Utils.ErrorThrow(ex, $"Error while extracting {source}");
                throw new Exception();
            }
        }

        private static async Task<ExtractedFiles> ExtractAllExe(AbsolutePath source)
        {
            var isArchive = await TestWith7z(source);

            if (isArchive)
            {
                return await ExtractAllWith7Zip(source, null);
            }

            var dest = await TempFolder.Create();
            Utils.Log($"Extracting {(string)source.FileName}");

            var process = new ProcessHelper
            {
                Path = @"Extractors\innounp.exe".RelativeTo(AbsolutePath.EntryPoint),
                Arguments = new object[] {"-x", "-y", "-b", $"-d\"{dest.Dir}\"", source}
            };

            
            var result = process.Output.Where(d => d.Type == ProcessHelper.StreamType.Output)
                .ForEachAsync(p =>
                {
                    var (_, line) = p;
                    if (line == null)
                        return;

                    if (line.Length <= 4 || line[3] != '%')
                        return;

                    int.TryParse(line.Substring(0, 3), out var percentInt);
                    Utils.Status($"Extracting {source.FileName} - {line.Trim()}", Percent.FactoryPutInRange(percentInt / 100d));
                });
            await process.Start();
            return new ExtractedFiles(dest);
        }

        private class OMODProgress : ICodeProgress
        {
            private long _total;

            public void SetProgress(long inSize, long outSize)
            {
                Utils.Status("Extracting OMOD", Percent.FactoryPutInRange(inSize, _total));
            }

            public void Init(long totalSize, bool compressing)
            {
                _total = totalSize;
            }

            public void Dispose()
            {
                //
            }
        }

        private static async Task<ExtractedFiles> ExtractAllWithOMOD(AbsolutePath source)
        {
            var dest = await TempFolder.Create();
            Utils.Log($"Extracting {(string)source.FileName}");

            Framework.Settings.TempPath = (string)dest.Dir;
            Framework.Settings.CodeProgress = new OMODProgress();

            var omod = new OMOD((string)source);
            omod.GetDataFiles();
            omod.GetPlugins();
            
            return new ExtractedFiles(dest);
        }


        private static async Task<ExtractedFiles> ExtractAllWithBSA(WorkQueue queue, AbsolutePath source)
        {
            try
            {
                var arch = await BSADispatch.OpenRead(source);
                var files = arch.Files.ToDictionary(f => f.Path, f => (IExtractedFile)new ExtractedBSAFile(f));
                return new ExtractedFiles(files);
            }
            catch (Exception ex)
            {
                Utils.ErrorThrow(ex, $"While Extracting {source}");
                throw new Exception();
            }
        }

        private static async Task<ExtractedFiles> ExtractAllWith7Zip(AbsolutePath source, IEnumerable<RelativePath> onlyFiles)
        {
            TempFile tmpFile = null;
            var dest = await TempFolder.Create();
            Utils.Log(new GenericInfo($"Extracting {(string)source.FileName}", $"The contents of {(string)source.FileName} are being extracted to {(string)source.FileName} using 7zip.exe"));

            var process = new ProcessHelper
            {
                Path = @"Extractors\7z.exe".RelativeTo(AbsolutePath.EntryPoint),
                
            };
            
            if (onlyFiles != null)
            {
                //It's stupid that we have to do this, but 7zip's file pattern matching isn't very fuzzy
                IEnumerable<string> AllVariants(string input)
                {
                    yield return $"\"{input}\"";
                    yield return $"\"\\{input}\"";
                }
                
                tmpFile = new TempFile();
                await tmpFile.Path.WriteAllLinesAsync(onlyFiles.SelectMany(f => AllVariants((string)f)).ToArray());
                process.Arguments = new object[]
                {
                    "x", "-bsp1", "-y", $"-o\"{dest.Dir}\"", source, $"@\"{tmpFile.Path}\"", "-mmt=off"
                };
            }
            else
            {
                process.Arguments = new object[] {"x", "-bsp1", "-y", $"-o\"{dest.Dir}\"", source, "-mmt=off"};
            }


            var result = process.Output.Where(d => d.Type == ProcessHelper.StreamType.Output)
                .ForEachAsync(p =>
                {
                    var (_, line) = p;
                    if (line == null)
                        return;

                    if (line.Length <= 4 || line[3] != '%') return;

                    int.TryParse(line.Substring(0, 3), out var percentInt);
                    Utils.Status($"Extracting {(string)source.FileName} - {line.Trim()}", Percent.FactoryPutInRange(percentInt / 100d));
                });

            var exitCode = await process.Start();

            
            if (exitCode != 0)
            {
                Utils.ErrorThrow(new _7zipReturnError(exitCode, source, dest.Dir, ""));
            }
            else
            {
                Utils.Status($"Extracting {source.FileName} - done", Percent.One, alsoLog: true);
            }

            if (tmpFile != null)
            {
                await tmpFile.DisposeAsync();
            }

            return new ExtractedFiles(dest);
        }

        /// <summary>
        ///     Returns true if the given extension type can be extracted
        /// </summary>
        /// <param name="v"></param>
        /// <returns></returns>
        public static async Task<bool> CanExtract(AbsolutePath v)
        {
            var found = await archiveSigs.MatchesAsync(v);
            switch (found)
            {
                case null:
                    return false;
                case Definitions.FileType.EXE:
                {
                    var process = new ProcessHelper
                    {
                        Path = @"Extractors\innounp.exe".RelativeTo(AbsolutePath.EntryPoint),
                        Arguments = new object[] {"-t", v},
                    };

                    return await process.Start() == 0;
                }
                default:
                    return true;
            }
        }

        public static async Task<bool> TestWith7z(AbsolutePath file)
        {
            var process = new ProcessHelper()
            {
                Path = @"Extractors\7z.exe".RelativeTo(AbsolutePath.EntryPoint),
                Arguments = new object[] {"t", file},
            };

            return await process.Start() == 0;
        }
        
        private static Extension _exeExtension = new Extension(".exe");
        
        public static bool MightBeArchive(Extension ext)
        {
            return ext == _exeExtension || Consts.SupportedArchives.Contains(ext) || Consts.SupportedBSAs.Contains(ext);
        }
    }
}
