﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Microsoft.SqlServer.Management.Smo;

namespace MigSharp.Generate
{
    internal class SqlMigrationGenerator
    {
        private readonly string _connectionString;
        private readonly string[] _excludedTables;
        private readonly List<string> _errors = new List<string>();

        public ReadOnlyCollection<string> Errors { get { return new ReadOnlyCollection<string>(_errors); } }

        public SqlMigrationGenerator(string connectionString, string[] excludedTables)
        {
            _connectionString = connectionString;
            _excludedTables = excludedTables;
        }

        public string Generate()
        {
            _errors.Clear();
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder(_connectionString);
            var server = new Server(builder.DataSource);
            Database database = server.Databases[builder.InitialCatalog];
            database.Refresh(); // load the meta-data
            string migration = string.Empty;

            // create tables
            var tablesWithForeignKeys = new List<Table>();
            foreach (Table table in database.Tables)
            {
                if (_excludedTables.Contains(table.Name) ||
                    table.Name.StartsWith("__", StringComparison.Ordinal) ||
                    table.Name == "MigSharp")
                {
                    // hide special tables such as the EF migration history table
                    Console.WriteLine("Skipping [{0}]", table.Name);
                    continue;
                }
                if (table.Schema != "dbo")
                {
                    Console.Error.WriteLine("WARNING: Skipping [{0}].[{1}] as non-dbo schemas are not supported yet.", table.Schema, table.Name);
                    continue;
                }

                HandleTable(table, ref migration);
                if (table.ForeignKeys.Count > 0)
                {
                    tablesWithForeignKeys.Add(table);
                }
            }

            // create foreign keys between tables
            foreach (var table in tablesWithForeignKeys)
            {
                foreach (ForeignKey foreignKey in table.ForeignKeys)
                {
                    HandleForeignKey(table, foreignKey, ref migration);
                }
            }
            return migration;
        }

        private void HandleTable(Table table, ref string migration)
        {
            AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}db.CreateTable(\"{1}\")", Indent(0), table.Name), ref migration);
            Column lastColumn = table.Columns.OfType<Column>().Last();
            foreach (Column column in table.Columns)
            {
                HandleColumn(table, column, column == lastColumn, ref migration);
            }
            foreach (Index index in table.Indexes)
            {
                HandleIndex(table, index, ref migration);
            }
            AppendLine(string.Empty, ref migration);
        }

        private static void HandleForeignKey(Table table, ForeignKey foreignKey, ref string migration)
        {
            AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}db.Tables[\"{1}\"].AddForeignKeyTo(\"{2}\")",
                Indent(0),
                table.Name,
                foreignKey.ReferencedTable), ref migration);
            ForeignKeyColumn lastColumn = foreignKey.Columns.Cast<ForeignKeyColumn>().Last();
            foreach (ForeignKeyColumn column in foreignKey.Columns)
            {
                AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}.Through(\"{1}\", \"{2}\"){3}",
                    Indent(1),
                    column.Name,
                    column.ReferencedColumn,
                    column == lastColumn ? ";" : string.Empty), ref migration);
            }
        }

        private static void HandleIndex(Table table, Index index, ref string migration)
        {
            if (index.IndexKeyType == IndexKeyType.DriPrimaryKey || index.IsUnique) return; // handled in HandleColumn

            string line = string.Format(CultureInfo.InvariantCulture, "{0}db.Tables[\"{1}\"].AddIndex()", Indent(0), table.Name);
            foreach (IndexedColumn column in index.IndexedColumns)
            {
                line += string.Format(CultureInfo.InvariantCulture, ".OnColumn(\"{0}\")", column.Name);
            }
            line += ";";
            AppendLine(line, ref migration);
        }

        private void HandleColumn(Table table, Column column, bool isLastColumn, ref string migration)
        {
            try
            {
                bool isRowVersionColumn = column.DataType.SqlDataType == SqlDataType.Timestamp;
                string columnKind = column.InPrimaryKey ? "PrimaryKey" :
                    (isRowVersionColumn ? "RowVersion" : string.Format(CultureInfo.InvariantCulture, "{0}Nullable", column.Nullable ? string.Empty : "Not"));
                string uniqueExpression = GetUniqueExpression(table, column);
                AppendLine(string.Format(CultureInfo.InvariantCulture, "{0}.With{1}Column(\"{2}\"{3}){4}{5}{6}{7}{8}",
                    Indent(1),
                    columnKind,
                    column.Name,
                    isRowVersionColumn ? string.Empty : ", " + GetDbTypeExpression(column),
                    isRowVersionColumn ? string.Empty : GetOfSize(column),
                    column.Identity ? ".AsIdentity()" : string.Empty,
                    uniqueExpression,
                    !string.IsNullOrEmpty(column.Default) ? ".HavingDefault(" + column.Default + ")" : string.Empty,
                    isLastColumn ? ";" : string.Empty), ref migration);
            }
            catch (NotSupportedException x)
            {
                _errors.Add(string.Format(CultureInfo.CurrentCulture, "In table {0} for column {1}: {2}", table.Name, column.Name, x.Message));
            }
        }

        private static string GetUniqueExpression(Table table, Column column)
        {
            if (column.InPrimaryKey) return string.Empty;

            Index uniqueIndex = FindUniqueIndex(table, column);
            if (uniqueIndex == null) return string.Empty;

            if (uniqueIndex.IndexedColumns.Count == 1)
            {
                return ".Unique()"; // no unique constraint name required
            }
            else
            {
                return string.Format(CultureInfo.InvariantCulture, ".Unique(\"{0}\")", uniqueIndex.Name);
            }
        }

        private static Index FindUniqueIndex(Table table, Column column)
        {
            return table.Indexes.Cast<Index>().SingleOrDefault(i => i.IsUnique && i.IndexedColumns.Cast<IndexedColumn>().Any(c => c.Name == column.Name));
        }

        private static string GetOfSize(Column column)
        {
            if (column.DataType.MaximumLength <= 0)
            {
                return string.Empty;
            }
            DbType dbType = Convert(column.DataType.SqlDataType);
            if (dbType == DbType.String || dbType == DbType.AnsiString || dbType == DbType.StringFixedLength || dbType == DbType.AnsiStringFixedLength)
            {
                return string.Format(CultureInfo.InvariantCulture, ".OfSize({0})", column.DataType.MaximumLength);
            }
            if (dbType == DbType.Decimal)
            {
                return string.Format(CultureInfo.InvariantCulture, ".OfSize({0}, {1})", column.DataType.NumericPrecision, column.DataType.NumericScale);
            }
            if (dbType == DbType.DateTime2 && column.DataType.MaximumLength != 8)
            {
                return string.Format(CultureInfo.InvariantCulture, ".OfSize({0})", column.DataType.MaximumLength);
            }
            return string.Empty;
        }

        private static string Indent(int count)
        {
            return new string('\t', count + 3);
        }

        private static void AppendLine(string line, ref string migration)
        {
            Console.WriteLine(line);
            migration += line + Environment.NewLine;
        }

        private static string GetDbTypeExpression(Column column)
        {
            DbType dbType = Convert(column.DataType.SqlDataType);
            return typeof(DbType).Name + "." + Enum.GetName(typeof(DbType), dbType);
        }

        private static DbType Convert(SqlDataType type)
        {
            Debug.Assert(type != SqlDataType.Timestamp, "This case is handled elsewhere as RowVersion column.");

            // see: https://msdn.microsoft.com/en-us/library/cc716729(v=vs.110).aspx
            switch (type)
            {
                case SqlDataType.BigInt:
                    return DbType.Int64;
                case SqlDataType.Binary:
                    return DbType.Binary;
                case SqlDataType.Bit:
                    return DbType.Boolean;
                case SqlDataType.Char:
                    return DbType.AnsiStringFixedLength;
                case SqlDataType.DateTime:
                    return DbType.DateTime;
                case SqlDataType.Decimal:
                    return DbType.Decimal;
                case SqlDataType.Float:
                    return DbType.Single;
                    //case SqlDataType.Image:
                    //    break;
                case SqlDataType.Int:
                    return DbType.Int32;
                    //case SqlDataType.Money:
                    //    break;
                case SqlDataType.NChar:
                    return DbType.StringFixedLength;
                    //case SqlDataType.NText:
                    //    break;
                case SqlDataType.NVarChar:
                    return DbType.String;
                case SqlDataType.NVarCharMax:
                    return DbType.String;
                    //case SqlDataType.Real:
                    //    break;
                    //case SqlDataType.SmallDateTime:
                    //    break;
                case SqlDataType.SmallInt:
                    return DbType.Int16;
                    //case SqlDataType.SmallMoney:
                    //    break;
                    //case SqlDataType.Text:
                    //    break;
                case SqlDataType.TinyInt:
                    return DbType.Byte;
                case SqlDataType.UniqueIdentifier:
                    return DbType.Guid;
                    //case SqlDataType.UserDefinedDataType:
                    //    break;
                    //case SqlDataType.UserDefinedType:
                    //    break;
                    //case SqlDataType.VarBinary:
                    //    break;
                case SqlDataType.VarBinaryMax:
                    return DbType.Binary;
                case SqlDataType.VarChar:
                    return DbType.AnsiString;
                    //case SqlDataType.VarCharMax:
                    //    break;
                    //case SqlDataType.Variant:
                    //    break;
                case SqlDataType.Xml:
                    return DbType.Xml;
                    //case SqlDataType.SysName:
                    //    break;
                    //case SqlDataType.Numeric:
                    //    break;
                case SqlDataType.Date:
                    return DbType.Date;
                case SqlDataType.Time:
                    return DbType.Time;
                case SqlDataType.DateTimeOffset:
                    return DbType.DateTimeOffset;
                case SqlDataType.DateTime2:
                    return DbType.DateTime2;
                    //case SqlDataType.UserDefinedTableType:
                    //    break;
                    //case SqlDataType.HierarchyId:
                    //    break;
                    //case SqlDataType.Geometry:
                    //    break;
                    //case SqlDataType.Geography:
                    //    break;
                default:
                    throw new NotSupportedException(string.Format(CultureInfo.CurrentCulture, "The column type {0} is not supported yet.", type));
            }
        }
    }
}