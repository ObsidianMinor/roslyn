﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.MSBuild.Build;
using Microsoft.CodeAnalysis.MSBuild.Logging;
using Roslyn.Utilities;
using MSB = Microsoft.Build;

namespace Microsoft.CodeAnalysis.VisualBasic
{
    internal class VisualBasicProjectFile : ProjectFile
    {
        public VisualBasicProjectFile(VisualBasicProjectFileLoader loader, MSB.Evaluation.Project loadedProject, ProjectBuildManager buildManager, DiagnosticLog log)
            : base(loader, loadedProject, buildManager, log)
        {
        }

        protected override SourceCodeKind GetSourceCodeKind(string documentFileName)
        {
            // TODO: uncomment when fixing https://github.com/dotnet/roslyn/issues/5325
            //return documentFileName.EndsWith(".vbx", StringComparison.OrdinalIgnoreCase)
            //    ? SourceCodeKind.Script
            //    : SourceCodeKind.Regular;
            return SourceCodeKind.Regular;
        }

        public override string GetDocumentExtension(SourceCodeKind sourceCodeKind)
        {
            // TODO: uncomment when fixing https://github.com/dotnet/roslyn/issues/5325
            //return (sourceCodeKind != SourceCodeKind.Script) ? ".vb" : ".vbx";
            return ".vb";
        }

        protected override IEnumerable<MSB.Framework.ITaskItem> GetCommandLineArgsFromModel(MSB.Execution.ProjectInstance executedProject)
        {
            return executedProject.GetItems(ItemNames.VbcCommandLineArgs);
        }

        protected override ProjectFileInfo CreateProjectFileInfo(MSB.Execution.ProjectInstance project)
        {
            var projectDirectory = GetProjectDirectory(project);

            var commandLineArgs = this.GetCommandLineArgsFromModel(project)
                .Select(item => item.ItemSpec)
                .Where(item => item.StartsWith("/"))
                .ToImmutableArray();

            if (commandLineArgs.Length == 0)
            {
                // We didn't get any command-line arguments. Try to read them directly from the project.
                commandLineArgs = ReadCommandLineArguments(project);
            }

            commandLineArgs = FixPlatform(commandLineArgs);

            var metadataReferences = this.GetMetadataReferencesFromModel(project)
                .ToImmutableArray();

            var outputFilePath = project.ReadPropertyString(PropertyNames.TargetPath);
            if (!string.IsNullOrWhiteSpace(outputFilePath))
            {
                outputFilePath = this.GetAbsolutePath(outputFilePath);
            }

            var outputRefFilePath = project.ReadPropertyString(PropertyNames.TargetRefPath);
            if (!string.IsNullOrWhiteSpace(outputRefFilePath))
            {
                outputRefFilePath = this.GetAbsolutePath(outputRefFilePath);
            }

            var targetFramework = project.ReadPropertyString(PropertyNames.TargetFramework);
            if (string.IsNullOrWhiteSpace(targetFramework))
            {
                targetFramework = null;
            }

            var docs = this.GetDocumentsFromModel(project)
                .Where(s => !IsTemporaryGeneratedFile(s.ItemSpec))
                .Select(s => MakeDocumentFileInfo(projectDirectory, s))
                .ToImmutableArray();

            var additionalDocs = this.GetAdditionalFilesFromModel(project)
                .Select(s => MakeAdditionalDocumentFileInfo(projectDirectory, s))
                .ToImmutableArray();

            return ProjectFileInfo.Create(
                this.Language,
                project.FullPath,
                outputFilePath,
                outputRefFilePath,
                targetFramework,
                commandLineArgs,
                docs,
                additionalDocs,
                this.GetProjectReferences(project),
                this.Log);
        }

        private ImmutableArray<string> FixPlatform(ImmutableArray<string> commandLineArgs)
        {
            string platform = null, target = null;
            var platformIndex = -1;

            for (int i = 0; i < commandLineArgs.Length; i++)
            {
                var arg = commandLineArgs[i];

                if (platform == null && arg.StartsWith("/platform:", StringComparison.OrdinalIgnoreCase))
                {
                    platform = arg.Substring("/platform:".Length);
                    platformIndex = i;
                }
                else if (target == null && arg.StartsWith("/target:", StringComparison.OrdinalIgnoreCase))
                {
                    target = arg.Substring("/target:".Length);
                }

                if (platform != null && target != null)
                {
                    break;
                }
            }

            if (string.Equals("anycpu32bitpreferred", platform, StringComparison.OrdinalIgnoreCase)
                && (string.Equals("library", target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals("module", target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals("winmdobj", target, StringComparison.OrdinalIgnoreCase)))
            {
                return commandLineArgs.SetItem(platformIndex, "/platform:anycpu");
            }

            return commandLineArgs;
        }

        private ImmutableArray<string> ReadCommandLineArguments(MSB.Execution.ProjectInstance project)
        {
            var builder = ImmutableArray.CreateBuilder<string>();

            var additionalFiles = this.GetAdditionalFilesFromModel(project);
            if (additionalFiles != null)
            {
                foreach (var additionalFile in additionalFiles)
                {
                    builder.Add($"/additionalfile:\"{this.GetDocumentFilePath(additionalFile)}\"");
                }
            }

            var analyzers = this.GetAnalyzerReferencesFromModel(project);
            if (analyzers != null)
            {
                foreach (var analyzer in analyzers)
                {
                    builder.Add($"/analyzer:\"{this.GetDocumentFilePath(analyzer)}\"");
                }
            }

            var baseAddress = project.ReadPropertyString(PropertyNames.BaseAddress);
            if (!string.IsNullOrWhiteSpace(baseAddress))
            {
                builder.Add($"/baseaddress:{baseAddress}");
            }

            var codeAnalysisRulSet = project.ReadPropertyString(PropertyNames.ResolvedCodeAnalysisRuleSet);
            if (!string.IsNullOrWhiteSpace(codeAnalysisRulSet))
            {
                builder.Add($"/ruleset:\"{codeAnalysisRulSet}\"");
            }

            var codePath = project.ReadPropertyInt(PropertyNames.CodePage);
            if (codePath != 0)
            {
                builder.Add($"/codepage:{codePath}");
            }

            var emitDebugInformation = project.ReadPropertyBool(PropertyNames.DebugSymbols);
            if (emitDebugInformation)
            {
                var debugType = project.ReadPropertyString(PropertyNames.DebugType);

                if (string.Equals(debugType, "none", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/debug");
                }
                else if (string.Equals(debugType, "pdbonly", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/debug:pdbonly");
                }
                else if (string.Equals(debugType, "full", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/debug:full");
                }
                else if (string.Equals(debugType, "portable", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/debug:portable");
                }
                else if (string.Equals(debugType, "embedded", StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/debug:embedded");
                }
            }

            var defineConstants = project.ReadPropertyString(PropertyNames.FinalDefineConstants);
            if (!string.IsNullOrWhiteSpace(defineConstants))
            {
                builder.Add($"/define:{defineConstants}");
            }

            var delaySign = project.ReadPropertyBool(PropertyNames.DelaySign);
            if (delaySign)
            {
                builder.Add("/delaysign");
            }

            var errorReport = project.ReadPropertyString(PropertyNames.ErrorReport);
            if (!string.IsNullOrWhiteSpace(errorReport))
            {
                builder.Add($"/errorreport:{errorReport.ToLower()}");
            }

            var features = project.ReadPropertyString(PropertyNames.Features);
            if (!string.IsNullOrWhiteSpace(features))
            {
                foreach (var feature in CompilerOptionParseUtilities.ParseFeatureFromMSBuild(features))
                {
                    builder.Add($"/features:{feature}");
                }
            }

            var fileAlignment = project.ReadPropertyString(PropertyNames.FileAlignment);
            builder.Add($"/filealign:{fileAlignment}");

            var documentationFile = project.ReadPropertyString(PropertyNames.DocFileItem);
            var generateDocumentation = project.ReadPropertyBool(PropertyNames.GenerateDocumentation);

            var hasDocumentationFile = !string.IsNullOrWhiteSpace(documentationFile);

            if (hasDocumentationFile || generateDocumentation)
            {
                if (hasDocumentationFile)
                {
                    builder.Add($"/doc:\"{documentationFile}\"");
                }
                else
                {
                    builder.Add("/doc");
                }
            }

            var highEntropyVA = project.ReadPropertyBool(PropertyNames.HighEntropyVA);
            if (highEntropyVA)
            {
                builder.Add("/highentropyva");
            }

            var imports = this.GetTaskItems(project, ItemNames.Import);
            if (imports != null)
            {
                var importsString = string.Join(",", imports.Select(item => item.ItemSpec.Trim()));
                builder.Add($"/imports:{importsString}");
            }

            var languageVersion = project.ReadPropertyString(PropertyNames.LangVersion);
            if (!string.IsNullOrWhiteSpace(languageVersion))
            {
                builder.Add($"/langversion:{languageVersion}");
            }

            var mainEntryPoint = project.ReadPropertyString(PropertyNames.StartupObject);
            if (!string.IsNullOrWhiteSpace(mainEntryPoint))
            {
                builder.Add($"/main:\"{mainEntryPoint}\"");
            }

            var moduleAssemblyName = project.ReadPropertyString(PropertyNames.ModuleAssemblyName);
            if (!string.IsNullOrWhiteSpace(moduleAssemblyName))
            {
                builder.Add($"/moduleassemblyname:\"{moduleAssemblyName}\"");
            }

            var noStandardLib = project.ReadPropertyBool(PropertyNames.NoCompilerStandardLib);
            if (noStandardLib)
            {
                builder.Add("/nostdlib");
            }

            var optimize = project.ReadPropertyBool(PropertyNames.Optimize);
            if (optimize)
            {
                builder.Add("/optimize");
            }

            var optionCompare = project.ReadPropertyString(PropertyNames.OptionCompare);
            if (string.Equals("binary", optionCompare, StringComparison.OrdinalIgnoreCase))
            {
                builder.Add("/optioncompare:binary");
            }
            else if (string.Equals("text", optionCompare, StringComparison.OrdinalIgnoreCase))
            {
                builder.Add("/optioncompare:text");
            }

            var optionExplicit = project.ReadPropertyBool(PropertyNames.OptionExplicit);
            if (!optionExplicit)
            {
                // default is on/true
                builder.Add("/optionexplicit-");
            }

            var optionInfer = project.ReadPropertyBool(PropertyNames.OptionInfer);
            if (optionInfer)
            {
                builder.Add("/optioninfer");
            }

            var optionStrict = project.ReadPropertyBool(PropertyNames.OptionStrict);
            if (optionStrict)
            {
                builder.Add("/optionstrict+");
            }
            else
            {
                builder.Add("/optionstrict-");
            }

            var optionStrictType = project.ReadPropertyString(PropertyNames.OptionStrictType);
            if (!string.IsNullOrWhiteSpace(optionStrictType))
            {
                builder.Add($"/optionstrict:{optionStrictType}");
            }

            var outputAssembly = this.GetItemString(project, PropertyNames.IntermediateAssembly);
            if (!string.IsNullOrWhiteSpace(outputAssembly))
            {
                builder.Add($"/out:\"{outputAssembly}\"");
            }

            var signAssembly = project.ReadPropertyBool(PropertyNames.SignAssembly);
            if (signAssembly)
            {
                var keyFile = project.ReadPropertyString(PropertyNames.KeyOriginatorFile);
                if (!string.IsNullOrWhiteSpace(keyFile))
                {
                    builder.Add($"/keyfile:\"{keyFile}\"");
                }

                var keyContainer = project.ReadPropertyString(PropertyNames.KeyContainerName);
                if (!string.IsNullOrWhiteSpace(keyContainer))
                {
                    builder.Add($"/keycontainer:\"{keyContainer}\"");
                }
            }

            var references = this.GetMetadataReferencesFromModel(project);
            if (references != null)
            {
                foreach (var reference in references)
                {
                    builder.Add($"/reference:\"{this.GetDocumentFilePath(reference)}\"");
                }
            }

            var removeIntegerChecks = project.ReadPropertyBool(PropertyNames.RemoveIntegerChecks);
            if (removeIntegerChecks)
            {
                builder.Add("/removeintchecks");
            }

            var rootNamespace = project.ReadPropertyString(PropertyNames.RootNamespace);
            if (!string.IsNullOrWhiteSpace(rootNamespace))
            {
                builder.Add($"/rootnamespace:\"{rootNamespace}\"");
            }

            var sdkPath = project.ReadPropertyString(PropertyNames.FrameworkPathOverride);
            if (!string.IsNullOrWhiteSpace(sdkPath))
            {
                builder.Add($"/sdkpath:\"{sdkPath}\"");
            }

            var subsystemVersion = project.ReadPropertyString(PropertyNames.SubsystemVersion);
            if (!string.IsNullOrWhiteSpace(subsystemVersion))
            {
                builder.Add($"/subsystemversion:{subsystemVersion}");
            }

            var targetCompactFramework = project.ReadPropertyBool(PropertyNames.TargetCompactFramework);
            if (targetCompactFramework)
            {
                builder.Add("/netcf");
            }

            var targetType = project.ReadPropertyString(PropertyNames.OutputType);
            if (!string.IsNullOrWhiteSpace(targetType))
            {
                builder.Add($"/target:{targetType}");
            }

            var platform = project.ReadPropertyString(PropertyNames.PlatformTarget);
            var prefer32bit = project.ReadPropertyBool(PropertyNames.Prefer32Bit);
            if (prefer32bit && (string.IsNullOrWhiteSpace(platform) || string.Equals("anycpu", platform, StringComparison.OrdinalIgnoreCase)))
            {
                platform = "anycpu32bitpreferred";
            }

            if (!string.IsNullOrWhiteSpace(platform))
            {
                builder.Add($"/platform:{platform}");
            }

            var disabledWarnings = project.ReadPropertyString(PropertyNames.NoWarn);
            if (!string.IsNullOrWhiteSpace(disabledWarnings))
            {
                builder.Add($"/nowarn:{disabledWarnings}");
            }

            var noWarnings = project.ReadPropertyBool(PropertyNames._NoWarnings);
            if (noWarnings)
            {
                builder.Add("/nowarn");
            }

            var treatWarningsAsErrors = project.ReadPropertyBool(PropertyNames.TreatWarningsAsErrors);
            if (treatWarningsAsErrors)
            {
                builder.Add("/warnaserror");
            }

            var vbRuntime = project.ReadPropertyString(PropertyNames.VbRuntime);
            if (!string.IsNullOrWhiteSpace(vbRuntime))
            {
                if (string.Equals("Default", vbRuntime, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/vbruntime+");
                }
                else if (string.Equals("Embed", vbRuntime, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/vbruntime*");
                }
                else if (string.Equals("None", vbRuntime, StringComparison.OrdinalIgnoreCase))
                {
                    builder.Add("/vbruntime-");
                }
                else
                {
                    builder.Add($"/vbruntime:\"{vbRuntime}\"");
                }
            }

            var warningsAsErrors = project.ReadPropertyString(PropertyNames.WarningsAsErrors);
            if (!string.IsNullOrWhiteSpace(warningsAsErrors))
            {
                builder.Add($"/warnaserror+:{warningsAsErrors}");
            }

            var warningsNotAsErrors = project.ReadPropertyString(PropertyNames.WarningsNotAsErrors);
            if (!string.IsNullOrWhiteSpace(warningsNotAsErrors))
            {
                builder.Add($"/warnaserror-:{warningsNotAsErrors}");
            }

            return builder.ToImmutable();
        }
    }
}
