﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Npgsql.Internal;
using Npgsql.Internal.TypeHandlers;
using Npgsql.Internal.TypeHandling;

namespace Robust.Benchmarks.Exporters;

public sealed class SQLExporter : IExporter
{
    public static readonly IExporter Default = new SQLExporter();

    private SQLExporter(){}

    public void ExportToLog(Summary summary, ILogger logger)
    {
        Export(summary, logger);
    }

    public IEnumerable<string> ExportToFiles(Summary summary, ILogger consoleLogger)
    {
        Export(summary, consoleLogger);
        return Array.Empty<string>();
    }

    private bool TryGetEnvironmentVariable(string name, ILogger logger, [NotNullWhen(true)] out string? value)
    {
        value = Environment.GetEnvironmentVariable(name);
        if (value == null)
            logger.WriteError($"ROBUST_BENCHMARKS_ENABLE_SQL is set, but {name} is missing.");
        return value != null;
    }

    private void Export(Summary summary, ILogger logger)
    {
        if (!TryGetEnvironmentVariable("ROBUST_BENCHMARKS_SQL_ADDRESS", logger, out var address) ||
            !TryGetEnvironmentVariable("ROBUST_BENCHMARKS_SQL_PORT", logger, out var rawPort) ||
            !TryGetEnvironmentVariable("ROBUST_BENCHMARKS_SQL_USER", logger, out var user) ||
            !TryGetEnvironmentVariable("ROBUST_BENCHMARKS_SQL_PASSWORD", logger, out var password) ||
            !TryGetEnvironmentVariable("ROBUST_BENCHMARKS_SQL_DATABASE", logger, out var db) ||
            !TryGetEnvironmentVariable("GITHUB_SHA", logger, out var gitHash))
            return;

        if (!int.TryParse(rawPort, out var port))
        {
            logger.WriteError("Failed parsing ROBUST_BENCHMARKS_SQL_PORT to int.");
            return;
        }

        var builder = new DbContextOptionsBuilder<BenchmarkContext>();
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = address,
            Port = port,
            Database = db,
            Username = user,
            Password = password
        }.ConnectionString;
        builder.UseNpgsql(connectionString);
        using var ctx = new BenchmarkContext(builder.Options);
        try
        {
            ctx.Database.OpenConnection();
            var con = (NpgsqlConnection) ctx.Database.GetDbConnection();
            con.TypeMapper.AddTypeResolverFactory(new JsonOverrideTypeHandlerResolverFactory(new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));

            ctx.Database.Migrate();
            ctx.BenchmarkRuns.Add(BenchmarkRun.FromSummary(summary, gitHash));
            ctx.SaveChanges();
        }
        finally
        {
            ctx.Dispose();
        }
    }

    public string Name => "sql";
}

// https://github.com/npgsql/efcore.pg/issues/1107#issuecomment-945126627
class JsonOverrideTypeHandlerResolverFactory : TypeHandlerResolverFactory
{
    private readonly JsonSerializerOptions _options;

    public JsonOverrideTypeHandlerResolverFactory(JsonSerializerOptions options)
        => _options = options;

    public override TypeHandlerResolver Create(NpgsqlConnector connector)
        => new JsonOverrideTypeHandlerResolver(connector, _options);

    public override string? GetDataTypeNameByClrType(Type clrType)
        => null;

    public override TypeMappingInfo? GetMappingByDataTypeName(string dataTypeName)
        => null;

    class JsonOverrideTypeHandlerResolver : TypeHandlerResolver
    {
        readonly JsonHandler _jsonbHandler;

        internal JsonOverrideTypeHandlerResolver(NpgsqlConnector connector, JsonSerializerOptions options)
            => _jsonbHandler ??= new JsonHandler(
                connector.DatabaseInfo.GetPostgresTypeByName("jsonb"),
                connector.TextEncoding,
                isJsonb: true,
                options);

        public override NpgsqlTypeHandler? ResolveByDataTypeName(string typeName)
            => typeName == "jsonb" ? _jsonbHandler : null;

        public override NpgsqlTypeHandler? ResolveByClrType(Type type)
            // You can add any user-defined CLR types which you want mapped to jsonb
            => type == typeof(JsonDocument) ? _jsonbHandler : null;

        public override TypeMappingInfo? GetMappingByDataTypeName(string dataTypeName)
            => null; // Let the built-in resolver do this
    }
}
public sealed class DesignTimeContextFactoryPostgres : IDesignTimeDbContextFactory<BenchmarkContext>
{
    public BenchmarkContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<BenchmarkContext>();
        optionsBuilder.UseNpgsql("Server=localhost");
        return new BenchmarkContext(optionsBuilder.Options);
    }
}

public class BenchmarkContext : DbContext
{
    public DbSet<BenchmarkRun> BenchmarkRuns { get; set; } = default!;

    public BenchmarkContext() { }
    public BenchmarkContext(DbContextOptions<BenchmarkContext> options) : base(options) { }
}

public class BenchmarkRun
{
    public int Id { get; set; }
    public string GitHash { get; set; } = string.Empty;

    [Column(TypeName = "timestamptz")]
    public DateTime RunDate { get; set; }

    public string Name { get; set; } = string.Empty;

    [Column(TypeName = "jsonb")]
    public BenchmarkRunReport[] Reports { get; set; } = Array.Empty<BenchmarkRunReport>();

    public static BenchmarkRun FromSummary(Summary summary, string gitHash)
    {
        return new BenchmarkRun
        {
            Reports = summary.Reports.Select(r => new BenchmarkRunReport
            {
                Parameters = r.BenchmarkCase.Parameters.Items.Select(p => new BenchmarkRunParameter
                {
                    Name = p.Name,
                    Value = p.Value
                }).ToArray(),
                Statistics = r.ResultStatistics
            }).ToArray(),
            Name = summary.BenchmarksCases.First().FolderInfo,
            RunDate = DateTime.UtcNow,
            GitHash = gitHash
        };
    }
}

public class BenchmarkRunReport
{
    public BenchmarkRunParameter[] Parameters { get; set; } = Array.Empty<BenchmarkRunParameter>();
    public Statistics Statistics { get; set; } = default!;
}

public class BenchmarkRunParameter
{
    public string Name { get; set; } = string.Empty;
    public object Value { get; set; } = default!;
}
