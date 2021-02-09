﻿// -----------------------------------------------------------------------
// <copyright file="BatchingSqlServerJournal.cs" company="Akka.NET Project">
//      Copyright (C) 2013 - 2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using Akka.Configuration;
using Akka.Persistence.Sql.Common.Journal;

namespace Akka.Persistence.SqlServer.Journal
{
    public sealed class BatchingSqlServerJournalSetup : BatchingSqlJournalSetup
    {
        public BatchingSqlServerJournalSetup(Config config) : base(
            config, 
            new QueryConfiguration(
                config.GetString("schema-name", "dbo"),
                config.GetString("table-name", "EventJournal"),
                config.GetString("metadata-table-name", "Metadata"),
                "PersistenceId",
                "SequenceNr",
                "Payload",
                "Manifest",
                "Timestamp",
                "IsDeleted",
                "Tags",
                "Ordering",
                "SerializerId",
                config.GetTimeSpan("connection-timeout", TimeSpan.FromSeconds(30)),
                config.GetString("serializer", null),
                config.GetBoolean("sequential-access", false)))
        {
        }

        public BatchingSqlServerJournalSetup(
            string connectionString, 
            int maxConcurrentOperations, 
            int maxBatchSize,
            int maxBufferSize, 
            bool autoInitialize,
            TimeSpan connectionTimeout, 
            IsolationLevel isolationLevel, 
            CircuitBreakerSettings circuitBreakerSettings,
            ReplayFilterSettings replayFilterSettings, 
            QueryConfiguration namingConventions, 
            string defaultSerialzier)
            : base(
                connectionString: connectionString, 
                maxConcurrentOperations: maxConcurrentOperations, 
                maxBatchSize: maxBatchSize, 
                maxBufferSize: maxBufferSize, 
                autoInitialize: autoInitialize,
                connectionTimeout: connectionTimeout, 
                isolationLevel: isolationLevel, 
                circuitBreakerSettings: circuitBreakerSettings, 
                replayFilterSettings: replayFilterSettings, 
                namingConventions: namingConventions,
                defaultSerializer: defaultSerialzier)
        {
        }
    }

    public class BatchingSqlServerJournal : BatchingSqlJournal<SqlConnection, SqlCommand>
    {
        public BatchingSqlServerJournal(Config config) : this(new BatchingSqlServerJournalSetup(config))
        { }

        public BatchingSqlServerJournal(BatchingSqlServerJournalSetup setup) : base(setup)
        {
            var connectionTimeoutSeconds =
                new SqlConnectionStringBuilder(setup.ConnectionString).ConnectTimeout;
            var commandTimeout = setup.ConnectionTimeout;
            var circuitBreakerTimeout = setup.CircuitBreakerSettings.CallTimeout;
            var totalTimeout = commandTimeout
                .Add(TimeSpan.FromSeconds(connectionTimeoutSeconds));
            if (totalTimeout >= circuitBreakerTimeout)
            {
                Log.Warning(
                    "Configured Total of Connection timeout ({0} seconds) and Command timeout ({1} seconds) is greater than or equal to Circuit breaker timeout ({2} seconds). This may cause unintended write failures",
                    connectionTimeoutSeconds, 
                    commandTimeout.TotalSeconds,
                    circuitBreakerTimeout.TotalSeconds);
            }

            var c = Setup.NamingConventions;
            var allEventColumnNames = $@"
                e.{c.PersistenceIdColumnName} as PersistenceId, 
                e.{c.SequenceNrColumnName} as SequenceNr, 
                e.{c.TimestampColumnName} as Timestamp, 
                e.{c.IsDeletedColumnName} as IsDeleted, 
                e.{c.ManifestColumnName} as Manifest, 
                e.{c.PayloadColumnName} as Payload,
                e.{c.SerializerIdColumnName} as SerializerId";

            ByTagSql = $@"
             DECLARE @Tag_sized NVARCHAR(100);
             SET @Tag_sized = @Tag;
             SELECT TOP (@Take)
             {allEventColumnNames}, e.{c.OrderingColumnName} as Ordering
             FROM {c.FullJournalTableName} e
             WHERE e.{c.OrderingColumnName} > @Ordering AND e.{c.TagsColumnName} LIKE @Tag_sized
             ORDER BY {c.OrderingColumnName} ASC
             ";

            AllEventsSql = $@"
            SELECT TOP (@Take)
            {allEventColumnNames}, e.{c.OrderingColumnName} as Ordering
            FROM {c.FullJournalTableName} e
            WHERE e.{c.OrderingColumnName} > @Ordering
            ORDER BY {c.OrderingColumnName} ASC";

            Initializers = ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["CreateJournalSql"] =
                    $@"IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{
                            c.SchemaName
                        }' AND TABLE_NAME = '{c.JournalEventsTableName}')
                BEGIN
                    CREATE TABLE {c.FullJournalTableName} (
                        {c.OrderingColumnName} BIGINT IDENTITY(1,1) NOT NULL,
	                    {c.PersistenceIdColumnName} NVARCHAR(255) NOT NULL,
	                    {c.SequenceNrColumnName} BIGINT NOT NULL,
                        {c.TimestampColumnName} BIGINT NOT NULL,
                        {c.IsDeletedColumnName} BIT NOT NULL,
                        {c.ManifestColumnName} NVARCHAR(500) NOT NULL,
	                    {c.PayloadColumnName} VARBINARY(MAX) NOT NULL,
                        {c.TagsColumnName} NVARCHAR(100) NULL,
                        {c.SerializerIdColumnName} INTEGER NULL,
                        CONSTRAINT PK_{c.JournalEventsTableName} PRIMARY KEY ({c.OrderingColumnName}),
                        CONSTRAINT UQ_{c.JournalEventsTableName} UNIQUE ({c.PersistenceIdColumnName}, {
                            c.SequenceNrColumnName
                        })
                    );
                    CREATE INDEX IX_{c.JournalEventsTableName}_{c.SequenceNrColumnName} ON {c.FullJournalTableName}({
                            c.SequenceNrColumnName
                        });
                    CREATE INDEX IX_{c.JournalEventsTableName}_{c.TimestampColumnName} ON {c.FullJournalTableName}({
                            c.TimestampColumnName
                        });
                END",
                ["CreateMetadataSql"] = $@"
                IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = '{
                        c.SchemaName
                    }' AND TABLE_NAME = '{c.MetaTableName}')
                BEGIN
                    CREATE TABLE {c.FullMetaTableName} (
	                    {c.PersistenceIdColumnName} NVARCHAR(255) NOT NULL,
	                    {c.SequenceNrColumnName} BIGINT NOT NULL,
                        CONSTRAINT PK_{c.MetaTableName} PRIMARY KEY ({c.PersistenceIdColumnName}, {
                        c.SequenceNrColumnName
                    })
                    );
                END"
            });
            
        }

        protected override string ByTagSql { get; }
        protected override string AllEventsSql { get; }

        protected override ImmutableDictionary<string, string> Initializers { get; }

        protected override SqlConnection CreateConnection(string connectionString)
        {
            return new SqlConnection(connectionString);
        }

        /// <inheritdoc />
        protected override async Task<ColumnSizesInfo> LoadColumnSizesInternal(DbConnection connection)
        {
            var conventions = Setup.NamingConventions;
            
            // create command that should list all columns in it's output
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {conventions.FullJournalTableName}";

            // start reading - no need to
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                // load columns metadata
                var results = LoadSchemaTableInfo(reader);

                return new ColumnSizesInfo()
                {
                    PersistenceIdColumnSize = (int)results.First(r => r["ColumnName"].ToString() == conventions.PersistenceIdColumnName)["ColumnSize"],
                    TagsColumnSize = (int)results.First(r => r["ColumnName"].ToString() == conventions.TagsColumnName)["ColumnSize"],
                    ManifestColumnSize = (int)results.First(r => r["ColumnName"].ToString() == conventions.ManifestColumnName)["ColumnSize"]
                };
            }
        }
        
        private static List<Dictionary<string, object>> LoadSchemaTableInfo(DbDataReader reader)
        {
            var results = new List<Dictionary<string, object>>();
            
            // iterate through the table schema and extract metadata
            DataTable schemaTable = reader.GetSchemaTable();
            foreach (DataRow row in schemaTable.Rows)
            {
                var dict = new Dictionary<string, object>();
                foreach (DataColumn col in schemaTable.Columns)
                {
                    dict.Add(col.ColumnName, row[col.Ordinal]);
                }
                results.Add(dict);
            }

            return results;
        }
    }
}