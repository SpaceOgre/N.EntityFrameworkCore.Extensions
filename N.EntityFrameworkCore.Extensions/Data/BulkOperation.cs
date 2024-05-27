﻿using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using N.EntityFrameworkCore.Extensions.Common;
using N.EntityFrameworkCore.Extensions.Sql;
using N.EntityFrameworkCore.Extensions.Util;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;

namespace N.EntityFrameworkCore.Extensions
{
    internal partial class BulkOperation<T> : IDisposable
    {
        internal SqlConnection Connection => DbTransactionContext.Connection;
        internal DbContext Context { get; }
        internal bool StagingTableCreated { get; set; }
        internal string StagingTableName { get; }
        internal string[] PrimaryKeyColumnNames { get; }
        internal BulkOptions Options { get; }
        internal Expression<Func<T, object>> InputColumns { get; }
        internal Expression<Func<T, object>> IgnoreColumns { get; }
        internal DbTransactionContext DbTransactionContext { get; }
        internal Type EntityType => typeof(T);
        internal SqlTransaction Transaction => DbTransactionContext.CurrentTransaction;
        internal TableMapping TableMapping { get; }
        internal IEnumerable<string> SchemaQualifiedTableNames => TableMapping.GetSchemaQualifiedTableNames();

        public BulkOperation(DbContext dbContext, BulkOptions options, Expression<Func<T, object>> inputColumns = null, Expression<Func<T, object>> ignoreColumns = null)
        {
            Context = dbContext;
            Options = options;
            InputColumns = inputColumns;
            IgnoreColumns = ignoreColumns;

            DbTransactionContext = new DbTransactionContext(dbContext, options.CommandTimeout);
            TableMapping = dbContext.GetTableMapping(typeof(T), options.EntityType);
            StagingTableName = CommonUtil.GetStagingTableName(TableMapping, options.UsePermanentTable, Connection);
            PrimaryKeyColumnNames = TableMapping.GetPrimaryKeyColumns().ToArray();
        }
        internal BulkInsertResult<T> BulkInsertStagingData(IEnumerable<T> entities, bool keepIdentity=false, bool useInternalId=false)
        {
            IEnumerable<string> columnsToInsert = GetStagingColumnNames(keepIdentity);
            string internalIdColumn = useInternalId ? Common.Constants.InternalId_ColumnName : null;
            Context.Database.CloneTable(SchemaQualifiedTableNames, StagingTableName, TableMapping.GetQualifiedColumnNames(columnsToInsert), internalIdColumn);
            StagingTableCreated = true;
            return DbContextExtensions.BulkInsert(entities, Options, TableMapping, Connection, Transaction, StagingTableName, columnsToInsert, SqlBulkCopyOptions.KeepIdentity, useInternalId);
        }
        internal BulkMergeResult<T> ExecuteMerge(Dictionary<long, T> entityMap, Expression<Func<T, T, bool>> mergeOnCondition, 
            bool autoMapOutput, bool keepIdentity, bool insertIfNotExists, bool update = false, bool delete = false)
        {
            var rowsInserted = new Dictionary<IEntityType, int>();
            var rowsUpdated = new Dictionary<IEntityType, int>();
            var rowsDeleted = new Dictionary<IEntityType, int>();
            var rowsAffected = new Dictionary<IEntityType, int>();
            var outputRows = new List<BulkMergeOutputRow<T>>();

            foreach (var entityType in TableMapping.EntityTypes)
            {
                rowsInserted[entityType] = 0;
                rowsUpdated[entityType] = 0;
                rowsDeleted[entityType] = 0;
                rowsAffected[entityType] = 0;

                CreateMergeStatement(mergeOnCondition, autoMapOutput, keepIdentity, insertIfNotExists, update, delete, entityType, out var columnsToOutput, out var mergeStatement);

                if (autoMapOutput)
                {
                    List<IProperty> allProperties =
                    [
                        ..TableMapping.GetEntityProperties(entityType, ValueGenerated.OnAdd).ToArray(),
                        ..TableMapping.GetEntityProperties(entityType, ValueGenerated.OnAddOrUpdate).ToArray()
                    ];

                    var bulkQueryResult = Context.BulkQuery(mergeStatement.Sql, Options);
                    rowsAffected[entityType] = bulkQueryResult.RowsAffected;

                    foreach (var result in bulkQueryResult.Results)
                    {
                        string action = (string)result[0];
                        outputRows.Add(new BulkMergeOutputRow<T>(action));

                        if (action == SqlMergeAction.Delete)
                        {
                            rowsDeleted[entityType]++;
                        }
                        else
                        {
                            int entityId = (int)result[1];
                            var entity = entityMap[entityId];
                            if (action == SqlMergeAction.Insert)
                            {
                                rowsInserted[entityType]++;
                                if (allProperties.Count != 0)
                                {
                                    var entityValues = GetMergeOutputValues(columnsToOutput, result, allProperties);
                                    Context.SetStoreGeneratedValues(entity, allProperties, entityValues);
                                }
                            }
                            else if (action == SqlMergeAction.Update)
                            {
                                rowsUpdated[entityType]++;
                                if (allProperties.Count != 0)
                                {
                                    var entityValues = GetMergeOutputValues(columnsToOutput, result, allProperties);
                                    Context.SetStoreGeneratedValues(entity, allProperties, entityValues);
                                }
                            }
                        }
                    }
                }
                else
                {
                    rowsAffected[entityType] = Context.Database.ExecuteSqlInternal(mergeStatement.Sql, Options.CommandTimeout);
                }
            }
            return new BulkMergeResult<T>
            {
                Output = outputRows,
                RowsAffected = rowsAffected.Values.LastOrDefault(),
                RowsDeleted = rowsDeleted.Values.LastOrDefault(),
                RowsInserted = rowsInserted.Values.LastOrDefault(),
                RowsUpdated = rowsUpdated.Values.LastOrDefault()
            };
        }
        private void CreateMergeStatement(Expression<Func<T, T, bool>> mergeOnCondition, bool autoMapOutput, bool keepIdentity, bool insertIfNotExists, bool update, bool delete, IEntityType entityType, out IEnumerable<string> columnsToOutput, out SqlStatement mergeStatement)
        {
            var columnsToInsert = TableMapping.GetColumnNames(entityType).Intersect(GetColumnNames(entityType));
            if (keepIdentity)
            {
                columnsToInsert = columnsToInsert.Union(TableMapping.GetPrimaryKeyColumns());
            }
            var columnsToUpdate = update ? TableMapping.GetColumnNames(entityType).Intersect(GetColumnNames(entityType)) : [];
            var autoGeneratedColumns = autoMapOutput ? TableMapping.GetAutoGeneratedColumns(entityType) : [];
            columnsToOutput = autoMapOutput ? GetMergeOutputColumns(autoGeneratedColumns, delete) : [];
            var deleteEntityType = TableMapping.EntityType == entityType && delete;

            string mergeOnConditionSql = insertIfNotExists ? CommonUtil<T>.GetJoinConditionSql(mergeOnCondition, PrimaryKeyColumnNames, "t", "s") : "1=2";
            bool toggleIdentity = keepIdentity && TableMapping.HasIdentityColumn;
            mergeStatement = SqlStatement.CreateMerge(StagingTableName, entityType.GetSchemaQualifiedTableName(),
                mergeOnConditionSql, columnsToInsert, columnsToUpdate, columnsToOutput, deleteEntityType, toggleIdentity);
        }
        private IEnumerable<string> GetMergeOutputColumns(IEnumerable<string> autoGeneratedColumns, bool delete = false)
        {
            List<string> columnsToOutput = new List<string> { "$Action", string.Format("[{0}].[{1}]", "s", Constants.InternalId_ColumnName) };
            columnsToOutput.AddRange(autoGeneratedColumns.Select(o => string.Format("[inserted].[{0}]", o)));
            return columnsToOutput.AsEnumerable();
        }
        private object[] GetMergeOutputValues(IEnumerable<string> columns, object[] values, IEnumerable<IProperty> properties)
        {
            var valuesIndex = properties.Select(o => columns.ToList().IndexOf($"[inserted].[{o.GetColumnName()}]"));
            return valuesIndex.Select(i => values[i]).ToArray();
        }
        internal int ExecuteUpdate(IEnumerable<T> entities, Expression<Func<T, T, bool>> updateOnCondition)
        {
            int rowsUpdated = 0;
            foreach (var entityType in TableMapping.EntityTypes)
            {
                IEnumerable<string> columnstoUpdate = CommonUtil.FormatColumns(GetColumnNames(entityType));
                string updateSetExpression = string.Join(",", columnstoUpdate.Select(o => string.Format("t.{0}=s.{0}", o)));
                string updateSql = string.Format("UPDATE t SET {0} FROM {1} AS s JOIN {2} AS t ON {3}; SELECT @@RowCount;",
                    updateSetExpression, StagingTableName, CommonUtil.FormatTableName(entityType.GetSchemaQualifiedTableName()), 
                    CommonUtil<T>.GetJoinConditionSql(updateOnCondition, PrimaryKeyColumnNames, "s", "t"));
                rowsUpdated = Context.Database.ExecuteSqlInternal(updateSql, Options.CommandTimeout);
            }
            return rowsUpdated;
        }
        internal void ValidateBulkMerge(Expression<Func<T, T, bool>> mergeOnCondition)
        {
            if (PrimaryKeyColumnNames.Length == 0 && mergeOnCondition == null)
                throw new InvalidDataException("BulkMerge requires that the entity have a primary key");
            if (PrimaryKeyColumnNames.Length == 0 && mergeOnCondition == null)
                throw new InvalidDataException("BulkMerge requires that Options.MergeOnCondition be set");
        }
        internal void ValidateBulkUpdate(Expression<Func<T, T, bool>> updateOnCondition)
        {
            if (PrimaryKeyColumnNames.Length == 0 && updateOnCondition == null)
                throw new InvalidDataException("BulkUpdate requires that the entity have a primary key or the Options.UpdateOnCondition must be set.");

        }
        public void Dispose()
        {
            if(StagingTableCreated)
            {
                Context.Database.DropTable(StagingTableName);
            }
        }
        internal IEnumerable<string> GetColumnNames(IEntityType entityType, bool keepIdentity=false)
        {
            IEnumerable<string> columnNames = CommonUtil.FilterColumns(TableMapping.GetColumns(keepIdentity), PrimaryKeyColumnNames, InputColumns, IgnoreColumns);
            return TableMapping.GetColumnNames(entityType).Intersect(columnNames);
        }
        internal IEnumerable<string> GetStagingColumnNames(bool keepIdentity=false)
        {
            IEnumerable<string> columnNames = CommonUtil.FilterColumns(TableMapping.GetColumns(keepIdentity), PrimaryKeyColumnNames, InputColumns, IgnoreColumns);
            return columnNames.Union(PrimaryKeyColumnNames);
        }
    }
}
