// Copyright (c) Mathias Thierbach
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.PowerBI.Api;
using Microsoft.Rest;
using Spectre.Console;
using AMO = Microsoft.AnalysisServices;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace PbiTools.Deployments
{
    using Model;
    using Utils;

    public partial class DeploymentManager
    {

        internal async Task DeployDatasetAsync(PbiDeploymentManifest manifest, string label, string environment)
        {
            #region Verify args
            if (!manifest.Environments.ContainsKey(environment))
                throw new DeploymentException($"The manifest does not contain the specified environment: {environment}.");

            var deploymentEnv = manifest.Environments[environment];

            if (deploymentEnv.Disabled)
            {
                Log.Warning("Deployment environment '{Environment}' disabled. Aborting.", environment);
                return;
            }
            #endregion

            Log.Information("Starting deployment '{DeploymentLabel}' into environment: {Environment} ...", label, environment);

            #region Resolve Deployment Sources
            var basePath = (BasePath == null)
                ? new FileInfo(Project.OriginalPath).DirectoryName
                : new DirectoryInfo(BasePath).FullName;

            Log.Write(WhatIfLogLevel, "Determining dataset from {SourceType} source: \"{SourcePath}\"", manifest.Source.Type, manifest.Source.Path);
            Log.Write(WhatIfLogLevel, "Base folder: {BasePath}", basePath);

            // Source: Folder | File
            var dataset = manifest.Source.Type switch
            {
                PbiDeploymentSourceType.Folder => GenerateDatasetFromFolderSource(manifest, deploymentEnv, basePath),
                PbiDeploymentSourceType.File => GetDatasetFromFileSource(manifest, deploymentEnv, basePath),
                _ => throw new DeploymentException($"Unsupported source type: '{manifest.Source.Type}'")
            };
            #endregion

            #region Get Auth Token
            if (manifest.Authentication.Type != PbiDeploymentAuthenticationType.ServicePrincipal)
                throw new DeploymentException("Only ServicePrincipal authentication is supported.");

            Log.Write(WhatIfLogLevel, "Acquiring access token...");
            AuthenticationResult authResult;
            var tokenProvider = PowerBITokenProviderFactory(manifest.Authentication.ExpandAndValidate());

            try
            {
                authResult = await tokenProvider.AcquireTokenAsync();
                Log.Write(WhatIfLogLevel, "Token Endpoint: {TokenEndpoint}", authResult?.AuthenticationResultMetadata.TokenEndpoint);
            }
            catch (MsalServiceException ex)
            {
                throw DeploymentException.From(ex);
            }

            var tokenCredentials = new TokenCredentials(authResult.AccessToken, authResult.TokenType);
            Log.Information("Access token received. Expires On: {ExpiresOn}", authResult.ExpiresOn);
            #endregion

            if (WhatIf) Log.Information("---");

            #region Connect to Destination Envionment
            using var powerbi = PowerBIClientFactory(manifest?.Options?.PbiBaseUri ?? DefaultPowerBIApiBaseUri, tokenCredentials);
            using var server = new TOM.Server();

            var workspace = deploymentEnv.Workspace.ExpandParameters(dataset.Parameters);
            var workspaceId = Guid.TryParse(workspace, out var id)
                ? id
                : await workspace.ResolveWorkspaceIdAsync(powerbi);

            if (WhatIf)
            {
                Log.Information("Dataset: {Path}", dataset.SourcePath);
                Log.Information("  DisplayName: {DisplayName}", dataset.DisplayName);
                Log.Information("  Parameters:");
                foreach (var parameter in dataset.Parameters)
                    Log.Information("  * {ParamKey} = {ParamValue}", parameter.Key, parameter.Value.Value ?? "null");
                Log.Information("  Workspace: {Workspace} ({WorkspaceId})", workspace, workspaceId);
                Log.Information("---");
            }

            var dataSource = deploymentEnv.XmlaDataSource ?? $"powerbi://api.powerbi.com/v1.0/myorg/{workspace}";
            var connectionStringBldr = new DbConnectionStringBuilder {
                { "Data Source", dataSource },
                { "Password", authResult.AccessToken }
            };

            Log.Information("Connecting to XMLA endpoint: {XmlaDataSource}", dataSource);
            server.Connect(connectionStringBldr.ConnectionString);

            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Debug) || WhatIf)
            {
                Log.Write(WhatIfLogLevel, "Server Properties:");
                Log.Write(WhatIfLogLevel, "* CompatibilityMode           : {CompatibilityMode}", server.CompatibilityMode);
                Log.Write(WhatIfLogLevel, "* ConnectionString            : {ConnectionString}", server.ConnectionString);
                Log.Write(WhatIfLogLevel, "* DefaultCompatibilityLevel   : {DefaultCompatibilityLevel}", server.DefaultCompatibilityLevel);
                Log.Write(WhatIfLogLevel, "* Edition                     : {Edition}", server.Edition);
                Log.Write(WhatIfLogLevel, "* ID                          : {ID}", server.ID);
                Log.Write(WhatIfLogLevel, "* Name                        : {Name}", server.Name);
                Log.Write(WhatIfLogLevel, "* SupportedCompatibilityLevels: {SupportedCompatibilityLevels}", server.SupportedCompatibilityLevels);
                Log.Write(WhatIfLogLevel, "* Version                     : {Version}", server.Version);
            }
            #endregion

            #region Build Sources
            Log.Write(WhatIfLogLevel, "Deserializing tabular model from sources...");
            var dbNew = TOM.JsonSerializer.DeserializeDatabase(dataset.Model.DataModel.ToString()
                , new TOM.DeserializeOptions { }
                , AMO.CompatibilityMode.PowerBI
            );

            static void LogDbInfo(TOM.Database db, string label, Serilog.Events.LogEventLevel level = Serilog.Events.LogEventLevel.Information) {
                Log.Write(level, label);
                Log.Write(level, "TOM Database Properties:");
                Log.Write(level, "* ID                    : {ID}", db.ID);
                Log.Write(level, "* Name                  : {Name}", db.Name);
                Log.Write(level, "* Description           : {Description}", db?.Model.Description);
                Log.Write(level, "* CompatibilityLevel    : {CompatibilityLevel}", db.CompatibilityLevel);
                Log.Write(level, "* CreatedTimestamp      : {CreatedTimestamp}", db.CreatedTimestamp);
                Log.Write(level, "* StructureModifiedTime : {StructureModifiedTime}", db?.Model.StructureModifiedTime);
                Log.Write(level, "* EstimatedSize         : {EstimatedSize}", db.EstimatedSize);
            };

            if (dataset.Options.Dataset.ReplaceParameters)
            {
                foreach (var modelExpr in dbNew.Model.Expressions.Where(e => e.Kind == TOM.ExpressionKind.M))
                {
                    if (dataset.Parameters.TryGetValue(modelExpr.Name, out var parameter)){
                        var newValue = parameter.ToMString();
                        Log.Information("Setting model expression '{Name}'\n\tOld value: {OldValue}\n\tNew value: {NewValue}"
                            , modelExpr.Name
                            , modelExpr.Expression
                            , newValue);
                        modelExpr.Expression = newValue;
                    }
                }
            }
            #endregion

            #region Init SqlScriptDeployer
            var artifactsFolder = new DeploymentArtifactsFolder(basePath, label);

            var sqlScripts = new SqlScriptsDeployer(
                dataset.Options.SqlScripts,
                dataset.Parameters,
                artifactsFolder,
                environment,
                basePath
            )
            { WhatIf = WhatIf };

            sqlScripts.TestConnection();

            sqlScripts.EnsureDatabase();

            #endregion

            #region Ensure Remote Database
            Log.Write(WhatIfLogLevel, "Checking for existing database with matching name...");
            var createdNewDb = false;
            var datasetId = default(string);

            if (!server.Databases.ContainsName(dataset.DisplayName))
            {
                using var newDb = new TOM.Database
                {
                    Name = dataset.DisplayName,
                    CompatibilityLevel = dbNew.CompatibilityLevel,
                    StorageEngineUsed = AMO.StorageEngineUsed.TabularMetadata
                };

                if (WhatIf)
                {
                    // Does NOT exist...
                    Log.Information("Workspace '{Workspace}' does not have existing dataset named '{DatasetName}'.", workspace, dataset.DisplayName);
                }
                else
                {
                    server.Databases.Add(newDb);
                    newDb.Update(AMO.UpdateOptions.ExpandFull);

                    Log.Information("Created new Power BI dataset: {ID}", newDb.ID);
                }

                createdNewDb = true;
            }
            else if (WhatIf)
            {
                // Database exists...
                using var db = server.Databases.GetByName(dataset.DisplayName);
                datasetId = db.ID;
                LogDbInfo(db, "Matching dataset found.");
            }
            #endregion

            sqlScripts.RunBeforeUpdate();

            #region Update Remote Database

            if (!WhatIf)
            {
                using var remoteDb = server.Databases.GetByName(dataset.DisplayName); // Database with specified name is guaranteed to exist at this point
                
                if (!createdNewDb)
                {
                    LogDbInfo(remoteDb, "Found existing dataset.");

                    // Ensure matching CompatibilityLevel
                    if (remoteDb.CompatibilityLevel != dbNew.CompatibilityLevel)
                    {
                        remoteDb.CompatibilityLevel = dbNew.CompatibilityLevel;
                        remoteDb.Update(AMO.UpdateOptions.ExpandFull);
                    }
                }

                dbNew.Name = dataset.DisplayName; // avoid name clash

                // TODO Modify partitions, roles, role members if required

                if (manifest.Options.Dataset.KeepRefreshPolicyPartitions)
                {
                    foreach (var table in dbNew.Model.Tables)
                    {
                        if (table.RefreshPolicy == null) continue;

                        Log.Information("Copying remote partitions for RefreshPolicy table: {Table}", table.Name);

                        var remoteTbl = remoteDb.Model.Tables.Find(table.Name);

                        if (remoteTbl == null) continue;

                        table.Partitions.Clear();

                        foreach (var partition in remoteTbl.Partitions)
                        {
                            Log.Information("- {Partition}", partition.Name);
                            var partitionJson = TOM.JsonSerializer.SerializeObject(partition);
                            table.Partitions.Add(TOM.JsonSerializer.DeserializeObject<TOM.Partition>(partitionJson));
                        }

                    }
                }

                // TODO Allow saving BIM as deployment artifact

                // Transfer new model schema...
                dbNew.Model.CopyTo(remoteDb.Model);

                Log.Debug("Updating model metadata...");
                var updateResults = remoteDb.Model.SaveChanges();
                // TODO Report updateResults.Impact?

                if (updateResults.XmlaResults != null && updateResults.XmlaResults.Count > 0)
                {
                    Log.Information("Update Results:");

                    foreach (var result in updateResults.XmlaResults.OfType<AMO.XmlaResult>())
                    {
                        Log.Information(result.Value);
                        foreach (var message in result.Messages.OfType<AMO.XmlaMessage>())
                            Log.Warning("- [{Severity}] {Description}\n\t{Location}\n--", message.GetType().Name, message.Description, message.Location?.SourceObject);
                    }
                }

                datasetId = remoteDb.ID;

                Log.Information("Model deployment succeeded.");

                if (manifest.Options.Dataset.ApplyRefreshPolicies)
                {
                    if (XmlaRefreshManager.TryGetEffectiveDateFromEnv(out var effectiveDate))
                    {
                        Log.Information("Applying refresh policies with effective date: {EffectiveDate}.", effectiveDate);
                        remoteDb.Model.ApplyRefreshPolicies(effectiveDate, refresh: false, refreshNonPolicyTables: false);
                    }
                    else
                    {
                        Log.Information("Applying refresh policies with current date.");
                        remoteDb.Model.ApplyRefreshPolicies(refresh: false, refreshNonPolicyTables: false);
                    }
                }

                ReportPartitionStatus(remoteDb.Model);
            }

            if (!WhatIf || !createdNewDb)
            {
                var pbiDataset = await powerbi.Datasets.GetDatasetInGroupAsync(workspaceId, datasetId);
                Log.Information("Power BI Dataset Details:");
                Log.Information("* ID                     : {ID}", pbiDataset.Id);
                Log.Information("* Name                   : {Name}", pbiDataset.Name);
                Log.Information("* WebUrl                 : {WebUrl}", pbiDataset.WebUrl);
                Log.Information("* ConfiguredBy           : {ConfiguredBy}", pbiDataset.ConfiguredBy);
                Log.Information("* CreatedDate            : {CreatedDate}", pbiDataset.CreatedDate);
                Log.Information("* IsRefreshable          : {IsRefreshable}", pbiDataset.IsRefreshable);
                Log.Information("* IsOnPremGatewayRequired: {IsOnPremGatewayRequired}", pbiDataset.IsOnPremGatewayRequired);
                Log.Information("* TargetStorageMode      : {TargetStorageMode}", pbiDataset.TargetStorageMode);
            }
            #endregion

            #region Report Datasources
            if (!WhatIf || !createdNewDb) {
                var dataSources = await powerbi.Datasets.GetDatasourcesInGroupAsync(workspaceId, datasetId);

                var table = new Spectre.Console.Table { Width = Environment.UserInteractive ? null : 80 };

                table.AddColumns(
                    nameof(Microsoft.PowerBI.Api.Models.Datasource.DatasourceType),
                    nameof(Microsoft.PowerBI.Api.Models.Datasource.ConnectionDetails),
                    nameof(Microsoft.PowerBI.Api.Models.Datasource.DatasourceId),
                    nameof(Microsoft.PowerBI.Api.Models.Datasource.GatewayId)
                );

                foreach (var item in dataSources.Value)
                {
                    table.AddRow(
                        $"{item.DatasourceType}",
                        $"{item.ConnectionDetails.ToJsonString(Newtonsoft.Json.Formatting.None)}",
                        $"{item.DatasourceId}",
                        $"{item.GatewayId}"
                    );
                }

                Log.Information("Datasources:");

                AnsiConsole.Write(table);
            }
            #endregion

            sqlScripts.RunAfterUpdate();

            #region Bind to Gateway (New dataset only)

            var gatewayManager = new DatasetGatewayManager(dataset.Options.Dataset.Gateway, powerbi) { WhatIf = WhatIf };

            await gatewayManager.DiscoverGatewaysAsync(workspaceId, datasetId);

            if (createdNewDb)
                await gatewayManager.BindToGatewayAsync(workspaceId, datasetId, dataset.Parameters);

            #endregion

            #region TODO: Set Dataset Permissions
            #endregion

            #region TODO: Set Role Members
            #endregion

            if (WhatIf) return; // TODO Determine further WhatIf stages...
                                // TODO Print report DisplayName in WhatIf mode...

            #region Set Credentials

            var credsManager = new DatasetCredentialsManager(manifest, powerbi) { WhatIf = WhatIf };
            await credsManager.SetCredentialsAsync(workspaceId, datasetId);

            #endregion

            #region Deploy Report
            if (manifest.Options.Dataset.DeployEmbeddedReport) {
                var reportEnvironment = deploymentEnv.Report ?? new();
                var pbixProjFolder = dataset.SourcePath;

                if (reportEnvironment.Skip) {
                    Log.Information("Report deployment is disabled for current environment. Skipping.");
                }
                else if (manifest.Source.Type != PbiDeploymentSourceType.Folder) {
                    Log.Warning("Report deployment is only supported if the deployment source is 'Folder'. Skipping.");
                }
                else if (!ProjectSystem.PbixProject.IsPbixProjFolder(pbixProjFolder)) {
                    Log.Warning("The deployment source is not a PbixProj folder: {Path}. Skipping report deployment.", pbixProjFolder);
                }
                else
                {
                    var reportConnection = manifest.Options.Report.CustomConnectionsTemplate == default
                        ? PowerBI.ReportConnection.CreateDefault()
                        : PowerBI.ReportConnection.Create(manifest.Options.Report.CustomConnectionsTemplate, basePath);

                    var reportDeploymentInfo = CompileReportForDeployment(manifest.Options,
                                                                          reportEnvironment.DisplayName.ExpandParameters(dataset.Parameters) ?? $"{dataset.DisplayName}.pbix",
                                                                          pbixProjFolder,
                                                                          manifest.ResolveTempDir(),
                                                                          dataset.Parameters,
                                                                          reportConnection.ToJson(datasetId));

                    var reportWorkspace = reportEnvironment.Workspace.ExpandParameters(dataset.Parameters) ?? workspaceId.ToString();
                    await ImportReportAsync(reportDeploymentInfo, powerbi, reportWorkspace);
                }
            }
            #endregion

            sqlScripts.RunSqlScripts();

            #region Refresh
            if (manifest.Options.Refresh.Enabled && deploymentEnv.Refresh?.Skip != true)
            {
                var stopWatch = System.Diagnostics.Stopwatch.StartNew();

                if (createdNewDb && dataset.Options.Refresh.SkipNewDataset) {
                    Log.Information("Skipping refresh because of the 'skipNewDataset' refresh option. You will likely need to set credentials and/or dataset gateways via Power BI Service first.");
                }
                else
                {
                    Log.Information("Starting dataset refresh ({RefreshType}) ...", dataset.Options.Refresh.Type);
                    switch (dataset.Options.Refresh.Method) {
                        case PbiDeploymentOptions.RefreshOptions.RefreshMethod.API:
                            throw new PbiToolsCliException(ExitCode.NotImplemented, "The 'API' refresh method is not implemented. Use 'XMLA' instead.");
                        case PbiDeploymentOptions.RefreshOptions.RefreshMethod.XMLA:

                            // The PBI server won't allow creating a Server trace unless the 'Initial Catalog' property is set
                            // Since the dataset might not exist at the start of the deployment, we're taking the safe route here
                            // and always reconnect with a new connection string at this point
                            Log.Debug("Reconnecting to server with 'Initial Catalog = {DatasetName}' connection string setting.", dataset.DisplayName);
                            connectionStringBldr.Add("Initial Catalog", dataset.DisplayName);
                            
                            server.Disconnect(endSession: false);
                            server.Connect(connectionStringBldr.ConnectionString, server.SessionID);

                            using (var db = server.Databases[datasetId]) {
                                new XmlaRefreshManager(db)
                                {
                                    BasePath = basePath,
                                    ManifestOptions = dataset.Options.Refresh,
                                    EnvironmentOptions = deploymentEnv.Refresh
                                }
                                .RunRefresh();

                                ReportPartitionStatus(db.Model);
                            }
                            break;
                        default:
                            throw new DeploymentException($"Invalid refresh method '{dataset.Options.Refresh.Method}'.");
                    }
                    Log.Information("Refresh completed in {Elapsed}", stopWatch.Elapsed);
                }
            }
            #endregion

            sqlScripts.RunAfterRefresh();

        }

        private static void ReportPartitionStatus(TOM.Model model)
        {
            var partitions = model.Tables
                .SelectMany(t => t.Partitions)
                .Select(p => new 
                {
                    Table = p.Table.Name,
                    Partition = p.Name,
                    p.Mode,
                    p.SourceType,
                    p.State,
                    p.ModifiedTime,
                    RangeStart = p.Source switch { TOM.PolicyRangePartitionSource policyRange => policyRange.Start.ToShortDateString() , _ => "" },
                    RangeEnd = p.Source switch { TOM.PolicyRangePartitionSource policyRange => policyRange.End.ToShortDateString() , _ => "" }
                })
                .ToArray();

            var table = new Spectre.Console.Table { Width = Environment.UserInteractive ? null : 80 };

            table.AddColumns(
                nameof(TOM.Table),
                nameof(TOM.Partition),
                nameof(TOM.Partition.State),
                nameof(TOM.Partition.SourceType),
                nameof(TOM.Partition.Mode),
                nameof(TOM.Partition.ModifiedTime),
                "RangeStart",
                "RangeEnd"
            );

            foreach (var item in partitions)
            {
                table.AddRow(
                    $"{item.Table}",
                    $"{item.Partition}",
                    $"{item.State}",
                    $"{item.SourceType}",
                    $"{item.Mode}",
                    $"{item.ModifiedTime}",
                    $"{item.RangeStart}",
                    $"{item.RangeEnd}"
                );
            }

            Log.Information("Partitions:");

            AnsiConsole.Write(table);
        }

        internal DatasetDeploymentInfo GetDatasetFromFileSource(PbiDeploymentManifest manifest, PbiDeploymentEnvironment deploymentEnv, string basePath)
        {
            // Assuming we have a BIM file
            var sourceFile = new FileInfo(Path.Combine(basePath, manifest.Source.Path));
            var converter = PbixModelConverter.FromFile(sourceFile);

            var parameters = DeploymentParameters.CalculateForEnvironment(
                manifest,
                deploymentEnv,
                (DeploymentParameters.Names.FILE_NAME, sourceFile.Name),
                (DeploymentParameters.Names.FILE_NAME_WITHOUT_EXT, Path.GetFileNameWithoutExtension(sourceFile.Name))
            );

            return GetDatasetInfo(manifest, converter.Model, sourceFile.FullName, parameters, parameters => deploymentEnv.DisplayName.ExpandParameters(parameters) ?? Path.GetFileNameWithoutExtension(sourceFile.Name));
        }

        internal DatasetDeploymentInfo GenerateDatasetFromFolderSource(PbiDeploymentManifest manifest, PbiDeploymentEnvironment deploymentEnv, string basePath)
        {
            // Assuming PbixProj or Model folder
            var sourceFolder = new DirectoryInfo(Path.Combine(basePath, manifest.Source.Path));
            var converter = PbixModelConverter.FromFolder(sourceFolder);

            var parameters = DeploymentParameters.CalculateForEnvironment(
                manifest, 
                deploymentEnv, 
                (DeploymentParameters.Names.PBIXPROJ_FOLDER, Path.GetFileName(converter.Model.SourcePath)),
                (DeploymentParameters.Names.FILE_NAME_WITHOUT_EXT, Path.GetFileName(converter.Model.SourcePath))
            );

            return GetDatasetInfo(manifest, converter.Model, sourceFolder.FullName, parameters, parameters => deploymentEnv.DisplayName.ExpandParameters(parameters) ?? sourceFolder.Name);
        }

        private DatasetDeploymentInfo GetDatasetInfo(
            PbiDeploymentManifest manifest, 
            IPbixModel model,
            string sourcePath,
            IDictionary<string, DeploymentParameter> parameters, 
            Func<IDictionary<string, DeploymentParameter>, string> resolveDisplayName
        ) =>
            new()
            {
                Model = model,
                SourcePath = sourcePath,
                DisplayName = resolveDisplayName(parameters),
                Options = manifest.Options,
                Parameters = new DeploymentParameters(parameters)
            };

        public class DatasetDeploymentInfo
        {
            /// <summary>
            /// The deployment options from the selected deployment profile.
            /// </summary>
            public PbiDeploymentOptions Options { get; set; }
            
            /// <summary>
            /// The effective deployment parameters for the target environment.
            /// </summary>
            public DeploymentParameters Parameters { get; set; }

            /// <summary>
            /// The folder or file where dataset sources reside.
            /// </summary>
            public string SourcePath { get; set; }

            /// <summary>
            /// The effective dataset name in the Power BI workspace.
            /// </summary>
            public string DisplayName { get; set; }

            /// <summary>
            /// The <see cref="IPbixModel"/> containing the dataset sources.
            /// </summary>
            public IPbixModel Model { get; set; }
        }

    }
}