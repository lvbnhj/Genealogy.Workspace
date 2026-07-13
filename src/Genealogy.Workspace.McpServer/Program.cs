using Genealogy.Workspace.Data;
using Genealogy.Workspace.Data.Repositories;
using Genealogy.Workspace.Data.Research;
using Genealogy.Workspace.Data.Resolvers;
using Genealogy.Workspace.Data.Staging;
using Genealogy.Workspace.Data.Traversal;
using Genealogy.Workspace.McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// MCP stdio transport uses stdout for JSON-RPC — all logs must go to stderr only
builder.Logging.ClearProviders();
builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddSingleton(WorkspaceDbOptions.FromEnvironment())
    .AddSingleton<NpgsqlConnectionFactory>()
    .AddSingleton<TreeRepository>()
    .AddSingleton<PersonRepository>()
    .AddSingleton<FamilyContextRepository>()
    .AddSingleton<PersonEventsRepository>()
    .AddSingleton<RichFamilyContextRepository>()
    .AddSingleton<PersonSearchRepository>()
    .AddSingleton<TreeTraversalRepository>()
    .AddSingleton<TreeResolver>()
    .AddSingleton<PersonResolver>()
    .AddSingleton<GedcomStagingService>()
    .AddSingleton<GedcomImportPreviewService>()
    .AddSingleton<GedcomDuplicateService>()
    .AddSingleton<GedcomReadinessService>()
    .AddSingleton<GedcomApplyService>()
    .AddSingleton(AttachmentOptions.FromEnvironment())
    .AddSingleton<AttachmentRepository>()
    .AddSingleton<SourceRecordRepository>()
    .AddSingleton<RecordMentionRepository>()
    .AddSingleton<PersonLinkService>()
    .AddSingleton<SourceRecordSearchRepository>()
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<TreeTools>()
    .WithTools<GedcomTools>()
    .WithTools<ResearchTools>()
    .WithTools<ResearchAttachmentTools>();

await builder.Build().RunAsync();
