// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.DotNet.Scaffolding.Helpers.Extensions.Roslyn;
using Microsoft.DotNet.Scaffolding.Helpers.General;
using Microsoft.DotNet.Scaffolding.Helpers.Roslyn;
using Microsoft.DotNet.Scaffolding.Helpers.Services;
using Microsoft.DotNet.Scaffolding.Helpers.Services.Environment;
using Microsoft.DotNet.Tools.Scaffold.Aspire.Helpers;
using Spectre.Console.Cli;

namespace Microsoft.DotNet.Tools.Scaffold.Aspire.Commands
{
    public class CachingCommand : AsyncCommand<CachingCommand.CachingCommandSettings>
    {
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IEnvironmentService _environmentService;
        //Dictionary to hold autogenerated project paths that are created during build-time for Aspire host projects.
        //The string key is the full project path (.csproj) and the string value is the full project name (with namespace
        private Dictionary<string, string> _autoGeneratedProjectNames;
        public CachingCommand(IFileSystem fileSystem, ILogger logger, IEnvironmentService environmentService)
        {
            _environmentService = environmentService;
            _fileSystem = fileSystem;
            _logger = logger;
            _autoGeneratedProjectNames = [];
        }

        public override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] CachingCommandSettings settings)
        {
            if (!ValidateCachingCommandSettings(settings))
            {
                return -1;
            }

            InstallPackages(settings);
            return await UpdateAppHostAsync(settings) && await UpdateWebAppAsync(settings) ? 0 : -1;
        }

        public class CachingCommandSettings : CommandSettings
        {
            [CommandOption("--type <TYPE>")]
            public required string Type { get; set; }

            [CommandOption("--apphost-project <APPHOSTPROJECT>")]
            public required string AppHostProject { get; set; }

            [CommandOption("--project <PROJECT>")]
            public required string Project { get; set; }
        }

        internal async Task<bool> UpdateAppHostAsync(CachingCommandSettings commandSettings)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            CodeModifierConfig? config = ProjectModifierHelper.GetCodeModifierConfig("redis-apphost.json", System.Reflection.Assembly.GetExecutingAssembly());
            var workspaceSettings = new WorkspaceSettings
            {
                InputPath = commandSettings.AppHostProject
            };

            var hostAppSettings = new AppSettings();
            hostAppSettings.AddSettings("workspace", workspaceSettings);
            var codeService = new CodeService(hostAppSettings, _logger);
            //initialize _autoGeneratedProjectNames here. 
            await GetAutoGeneratedProjectNamesAsync(codeService, commandSettings.Project);

            CodeChangeOptions options = new()
            {
                IsMinimalApp = await ProjectModifierHelper.IsMinimalApp(codeService),
                UsingTopLevelsStatements = await ProjectModifierHelper.IsUsingTopLevelStatements(codeService)
            };

            //edit CodeModifierConfig to add the web project name from _autoGeneratedProjectNames.
            _autoGeneratedProjectNames.TryGetValue(commandSettings.Project, out var autoGenProjectName);
            config = EditConfigForCaching(config, options, autoGenProjectName);

            var projectModifier = new ProjectModifier(
                _environmentService,
                hostAppSettings,
                codeService,
                _logger,
                options,
                config);
            return await projectModifier.RunAsync();
        }

        internal async Task GetAutoGeneratedProjectNamesAsync(CodeService codeService, string projectPath)
        {
            var allDocuments = await codeService.GetAllDocumentsAsync();
            var allSyntaxRoots = await Task.WhenAll(allDocuments.Select(doc => doc.GetSyntaxRootAsync()));

            // Get all classes with the "Projects" namespace
            var classesInNamespace = allSyntaxRoots
                .SelectMany(root => root?.DescendantNodes().OfType<ClassDeclarationSyntax>() ?? Enumerable.Empty<ClassDeclarationSyntax>())
                .Where(cls => cls.IsInNamespace("Projects"))
                .ToList();

            foreach (var classSyntax in classesInNamespace)
            {
                string? projectPathValue = classSyntax.GetStringPropertyValue("ProjectPath");
                // Get the full class name including the namespace
                var className = classSyntax.Identifier.Text;
                if (!string.IsNullOrEmpty(projectPathValue))
                {
                    _autoGeneratedProjectNames.Add(projectPathValue, $"Projects.{className}");
                }
            }
        }

        internal async Task<bool> UpdateWebAppAsync(CachingCommandSettings commandSettings)
        {
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            CodeModifierConfig? config = ProjectModifierHelper.GetCodeModifierConfig("redis-webapp.json", System.Reflection.Assembly.GetExecutingAssembly());
            var workspaceSettings = new WorkspaceSettings
            {
                InputPath = commandSettings.Project
            };

            var webAppSettings = new AppSettings();
            webAppSettings.AddSettings("workspace", workspaceSettings);
            var codeService = new CodeService(webAppSettings, _logger);
            
            CodeChangeOptions options = new()
            {
                IsMinimalApp = await ProjectModifierHelper.IsMinimalApp(codeService),
                UsingTopLevelsStatements = await ProjectModifierHelper.IsUsingTopLevelStatements(codeService)
            };

            var projectModifier = new ProjectModifier(
            _environmentService,
            webAppSettings,
            codeService,
            _logger,
            options,
            config);
            return await projectModifier.RunAsync();
        }

        internal bool ValidateCachingCommandSettings(CachingCommandSettings commandSettings)
        {
            if (string.IsNullOrEmpty(commandSettings.Type) || !GetCmdsHelper.CachingTypeCustomValues.Contains(commandSettings.Type, StringComparer.OrdinalIgnoreCase))
            {
                string cachingTypeDisplayList = string.Join(", ", GetCmdsHelper.CachingTypeCustomValues.GetRange(0, GetCmdsHelper.CachingTypeCustomValues.Count - 1)) +
                    (GetCmdsHelper.CachingTypeCustomValues.Count > 1 ? " and " : "") + GetCmdsHelper.CachingTypeCustomValues[GetCmdsHelper.CachingTypeCustomValues.Count - 1];
                _logger.LogMessage("Missing/Invalid --type option.", LogMessageType.Error);
                _logger.LogMessage($"Valid options : {cachingTypeDisplayList}", LogMessageType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(commandSettings.AppHostProject))
            {
                _logger.LogMessage("Missing/Invalid --apphost-project option.", LogMessageType.Error);
                return false;
            }

            if (string.IsNullOrEmpty(commandSettings.Project))
            {
                _logger.LogMessage("Missing/Invalid --project option.", LogMessageType.Error);
                return false;
            }

            return true;
        }

        internal void InstallPackages(CachingCommandSettings commandSettings)
        {
            if (_fileSystem.FileExists(commandSettings.AppHostProject))
            {
                DotnetCommands.AddPackage(
                    packageName: PackageConstants.CachingPackages.AppHostRedisPackageName,
                    logger: _logger,
                    projectFile: commandSettings.AppHostProject);
            }
            
            PackageConstants.CachingPackages.CachingPackagesDict.TryGetValue(commandSettings.Type, out string? projectPackageName);
            if (_fileSystem.FileExists(commandSettings.Project) && !string.IsNullOrEmpty(projectPackageName))
            {
                DotnetCommands.AddPackage(
                    packageName: projectPackageName,
                    logger: _logger,
                    projectFile: commandSettings.Project);
            }
        }

        // Currently only formatting the 'builder.AddProject<{0}>' Parent value in one of the CodeChange's in redis-apphost.json' 
        internal CodeModifierConfig? EditConfigForCaching(CodeModifierConfig? configToEdit, CodeChangeOptions codeChangeOptions, string? projectName)
        {
            if (configToEdit is null || string.IsNullOrEmpty(projectName))
            {
                return null;
            }

            var programCsFile = configToEdit.Files?.FirstOrDefault(x => !string.IsNullOrEmpty(x.FileName) && x.FileName.Equals("Program.cs", StringComparison.OrdinalIgnoreCase));
            if (programCsFile != null && programCsFile.Methods != null && programCsFile.Methods.Count != 0)
            {
                
                var addMethodMapping = programCsFile.Methods.Where(x => x.Key.Equals("Global", StringComparison.OrdinalIgnoreCase)).First().Value;
                var addProjectChange = addMethodMapping?.CodeChanges?.FirstOrDefault(x => !string.IsNullOrEmpty(x.Parent) && x.Parent.Contains("builder.AddProject<{0}>"));
                if (!codeChangeOptions.UsingTopLevelsStatements && addProjectChange != null)
                {
                    addProjectChange = DocumentBuilder.AddLeadingTriviaSpaces(addProjectChange, spaces: 12);
                }

                if (addProjectChange != null && !string.IsNullOrEmpty(addProjectChange.Parent))
                {
                    //update the parent value with the project name inserted.
                    addProjectChange.Parent = string.Format(addProjectChange.Parent, projectName);
                }
            }

            return configToEdit;
        }
    }
}
