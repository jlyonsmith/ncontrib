﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using NContrib.Extensions;

namespace NContrib {

    public struct FluidSqlEventHandler<T> {

        public Action<FluidSql, T> Handler { get; private set; }

        public FluidSqlEventHandler(Action<FluidSql, T> handler) : this() {
            Handler = handler;
        }
    }

    public class CommandExecutedEventArgs : EventArgs {

        public TimeSpan TimeTaken { get; protected set; }

        public SqlCommand Command { get; protected set; }

        public CommandExecutedEventArgs(TimeSpan timeTaken, SqlCommand command)
        {
            TimeTaken = timeTaken;
            Command = command;
        }
    }

    public class FluidSql {

        public static class BuiltinHandlers {
            
            public static string FormatProcedureError(FluidSql fs, SqlException ex) {

                var args = fs.Parameters.Select(p => "@" + p.Key + " = " + p.Value).Join(", ");

                return "Error executing procedure " + fs.Command.CommandText + " (" + args + "): " + ex.Message;
            }
        }

        public static class FieldNameConverters {
            
            public static string TitleCase(string name) {
                return System.Threading.Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(name).Replace("_", "");
            }

            public static string CamelCase(string name) {
                return name.ToCamelCase();
            }
        }

        public enum CrudOperation { Select, Insert, Update, Delete }

        protected readonly Stopwatch ExecutionTimer;

        public SqlConnection Connection { get; protected set; }

        public SqlCommand Command { get; protected set; }

        /// <summary>
        /// <see cref="Connection"/> is automatically closed after a command excution
        /// </summary>
        public bool AutoClose { get; protected set; }

        public int CommandExecutionCount { get; protected set; }

        public int RecordsAffected { get; protected set; }

        public event EventHandler<CommandExecutedEventArgs> Executed;

        protected CrudOperation TextCommandType { get; set; }
        protected string TableName { get; set; }
        protected string WhereClause { get; set; }

        protected readonly IDictionary<string, object> Parameters = new Dictionary<string, object>();

        protected SqlParameter ReturnValueParameter { get; set; }

        protected IList<SqlParameter> OutputParameters { get; set; }

        protected List<FluidSqlEventHandler<SqlException>> ErrorHandlers = new List<FluidSqlEventHandler<SqlException>>();

        protected List<FluidSqlEventHandler<SqlInfoMessageEventArgs>> InfoHandlers = new List<FluidSqlEventHandler<SqlInfoMessageEventArgs>>();

        protected List<FluidSqlEventHandler<StateChangeEventArgs>> ConnectionStateChangeHandlers = new List<FluidSqlEventHandler<StateChangeEventArgs>>();

        public FluidSql(string connectionString, bool autoClose = true)
            : this(new SqlConnection(connectionString)) {

            AutoClose = autoClose;
            OutputParameters = new List<SqlParameter>();
        }

        public FluidSql(SqlConnection connection) {
            Connection = connection;
            ExecutionTimer = new Stopwatch();
        }

        public FluidSql Error(Action<FluidSql, SqlException> handler) {
            ErrorHandlers.Add(new FluidSqlEventHandler<SqlException>(handler));
            return this;
        }

        public FluidSql Info(Action<FluidSql, SqlInfoMessageEventArgs> handler) {
            InfoHandlers.Add(new FluidSqlEventHandler<SqlInfoMessageEventArgs>(handler));
            return this;
        }

        public FluidSql ConnectionStateChange(Action<FluidSql, StateChangeEventArgs> handler) {
            ConnectionStateChangeHandlers.Add(new FluidSqlEventHandler<StateChangeEventArgs>(handler));
            return this;
        }

        public FluidSql ChangeDatabase(string db) {
            OpenConnection();
            Connection.ChangeDatabase(db);
            return this;
        }

        public FluidSql ExecutedHandler(EventHandler<CommandExecutedEventArgs> handler) {
            Executed += handler;
            return this;
        }

        public FluidSql AddParameter(string name, object value) {

            Parameters.Add(name.ToSnakeCase(), value);

            RegenerateInsertSql();
            RegenerateUpdateSql();

            return this;
        }

        public FluidSql AddParameters(object parameters) {
            if (parameters == null)
                return this;

            return AddParameters(parameters.GetType().GetProperties()
                .ToDictionary(p => p.Name, p => p.GetValue(parameters, null)));
        }

        public FluidSql AddParameters(IDictionary<string, object> parameters) {
            parameters.ToList().ForEach(p => AddParameter(p.Key, p.Value));
            return this;
        }

        public FluidSql RemoveParameter(string name) {

            Parameters.Remove(name);

            RegenerateInsertSql();
            RegenerateUpdateSql();

            return this;
        }

        public FluidSql RemoveNullParameters() {

            Parameters.Where(p => p.Value == null).Action(p => RemoveParameter(p.Key));
            return this;
        }

        public FluidSql RemoveBlankParameters() {

            Parameters
                .Where(p => p.Value is string)
                .Where(p => string.IsNullOrEmpty((string) p.Value))
                .Action(p => RemoveParameter(p.Key));

            return this;
        }

        public FluidSql RemoveNullAndBlankParameters() {

            RemoveNullParameters();
            RemoveBlankParameters();
            return this;
        }

        public FluidSql AddOutputParameter(string name, SqlDbType type) {

            OutputParameters.Add(new SqlParameter(name, type, -1) {Direction = ParameterDirection.Output});
            return this;
        }

        public T GetOutputParameter<T>(string name) {
            return OutputParameters.Single(p => p.ParameterName == name).Value.ConvertTo<T>();
        }

        public T GetReturnValue<T>() {
            if (ReturnValueParameter == null)
                throw new Exception("No return value parameter has been initialized");

            return ReturnValueParameter.Value.ConvertTo<T>();
        }

        public string DescribeCommand()
        {
            if (Command.CommandType == CommandType.Text || Command.CommandType == CommandType.TableDirect)
                return Command.CommandText;

            var paramDescription = Command.Parameters.Cast<SqlParameter>()
                                          .Where(p => p.Direction != ParameterDirection.ReturnValue)
                                          .Select(p => "@" + p.ParameterName + " = " + p.Value)
                                          .Join(", ");

            var description = "exec " + Command.CommandText;

            if (paramDescription.IsNotBlank())
                description += " " + paramDescription;

            return description;
        }

        #region Public setup
        public FluidSql CreateProcedureCommand(string procedureName, object parameters = null) {
            CreateCommand(procedureName, CommandType.StoredProcedure, parameters);
            return this;
        }

        public FluidSql CreateTextCommand(string textCommand, object parameters = null) {
            CreateCommand(textCommand, CommandType.Text, parameters);
            return this;
        }

        public FluidSql CreateInsertCommand(string table, object fields) {

            return CreateInsertCommand(table, ObjectToFieldDictionary(fields));
        }

        public FluidSql CreateInsertCommand(string table, IDictionary<string, object> fields) {

            AddParameters(fields);

            TableName = table;
            TextCommandType = CrudOperation.Insert;
            
            RegenerateInsertSql();

            return this;
        }

        public FluidSql CreateUpdateCommand(string table, object fields, string where) {
            
            return CreateUpdateCommand(table, ObjectToFieldDictionary(fields), where);
        }

        public FluidSql CreateUpdateCommand(string table, IDictionary<string, object> fields, string where) {

            
            AddParameters(fields);

            TableName = table;
            WhereClause = where;
            TextCommandType = CrudOperation.Update;

            RegenerateUpdateSql();

            return this;
        }
        
        #endregion

        protected static IDictionary<string, object> ObjectToFieldDictionary(object o) {

            return o.GetType()
                .GetProperties()
                .ToDictionary(p => p.Name.ToSnakeCase(), p => p.GetValue(o, null));
        }

        protected void RegenerateInsertSql() {

            if (TextCommandType != CrudOperation.Insert)
                return;

            var sql = "insert into " + TableName +
                " (" + Parameters.Keys.Join(", ") + ")" +
                " values(" + Parameters.Keys.Select(k => "@" + k).Join(", ") + ")";

            CreateTextCommand(sql);
        }

        protected void RegenerateUpdateSql() {

            if (TextCommandType != CrudOperation.Update)
                return;

            var sql = "update " + TableName + " set " + Parameters.Keys.Select(f => f + " = @" + f).Join(", ") + " where " + WhereClause;

            CreateTextCommand(sql);
        }

        #region Public execution
        public FluidSql ExecuteNonQuery() {
            InternalExecuteNonQuery();
            return this;
        }

        public int ExecuteRecordsAffected() {
            return InternalExecuteNonQuery();
        }

        public T ExecuteReturnValue<T>() {
            InternalExecuteNonQuery();
            return GetReturnValue<T>();
        }

        public T ExecuteScalar<T>() {
            return InternalExecuteScalar().ConvertTo<T>();
        }

        public T ExecuteScalar<T>(string commandText, object parameters = null) {
            CreateCommand(commandText, CommandType.Text, parameters);
            return InternalExecuteScalar().ConvertTo<T>();
        }

        public List<IDictionary<string, object>> ExecuteDictionaries(Func<string, string> fieldNameConverter = null) {
            return ExecuteDictionaries<object>(fieldNameConverter);
        }

        public List<IDictionary<string, TValue>> ExecuteDictionaries<TValue>(Func<string, string> fieldNameConverter = null) {
            var temp = ExecuteAndTransform(dr => dr.GetRowAsDictionary<TValue>(fieldNameConverter));
            OnDataRead();
            return temp;
        }

        public Dictionary<TKey, TValue> ExecuteVerticalDictionary<TKey, TValue>(int keyCol = 0, int valCol = 1) {
            var temp = new Dictionary<TKey, TValue>();
            using (var dr = InternalExecuteReader()) {
                while (dr.Read())
                    temp.Add(dr.GetValue<TKey>(keyCol), dr.GetValue<TValue>(valCol));
            }
            OnDataRead();
            return temp;
        }

        public Dictionary<TKey, TValue> ExecuteVerticalDictionary<TKey, TValue>(string keyCol, string valCol) {
            var temp = new Dictionary<TKey, TValue>();
            using (var dr = InternalExecuteReader()) {
                while (dr.Read())
                    temp.Add(dr.GetValue<TKey>(keyCol), dr.GetValue<TValue>(valCol));
            }
            OnDataRead();
            return temp;
        }

        public ILookup<TKey, TValue> ExecuteVerticalLookup<TKey, TValue>(int keyCol = 0, int valCol = 0) {

            return ExecuteAndTransform(r => new {Key = r.GetValue<TKey>(keyCol), Value = r.GetValue<TValue>(valCol)})
                .ToLookup(o => o.Key, o => o.Value);
        }

        public ILookup<TKey, TValue> ExecuteVerticalLookup<TKey, TValue>(string keyCol, string valCol) {

            return ExecuteAndTransform(r => new { Key = r.GetValue<TKey>(keyCol), Value = r.GetValue<TValue>(valCol) })
                .ToLookup(o => o.Key, o => o.Value);
        }

        public T[] ExecuteArray<T>() {
            return ExecuteAndTransform(r => r.GetValue<T>(0)).ToArray();
        }

        public T[] ExecuteArray<T>(int keyCol) {
            return ExecuteAndTransform(r => r.GetValue<T>(keyCol)).ToArray();
        }

        public T[] ExecuteArray<T>(string keyCol) {
            return ExecuteAndTransform(r => r.GetValue<T>(keyCol)).ToArray();
        }

        public List<T> ExecuteAndTransform<T>(Converter<IDataReader, T> converter) {
            List<T> temp;
            using (var dr = InternalExecuteReader()) {
                temp = dr.TransformAll(converter);
            }
            OnDataRead();
            return temp;
        }

        public List<T> ExecuteAndAutoMap<T>()
        {
            return ExecuteAndTransform(dr => dr.AutoMapType<T>());
        } 

        public T ExecuteScopeIdentity<T>() {
            Command.CommandText += "; select scope_identity()";
            return ExecuteScalar<T>();
        }
        
        public void ExecuteBinaryStream(string columnName, Stream output, int bufferSize = 1 << 18) {
            
            using (var dr = InternalExecuteReader(CommandBehavior.SequentialAccess)) {

                dr.Read();

                var colId = dr.GetOrdinal(columnName);

                long bytesRead;
                long position = 0;
                var buffer = new byte[bufferSize];

                while ( (bytesRead = dr.GetBytes(colId, position, buffer, 0, buffer.Length)) > 0 ) {

                    position += bytesRead;
                    output.Write(buffer, 0, (int)bytesRead);
                }
            }
        }

        #endregion

        #region Inline value assignment
        /*
         * These need to be re-worked as post-execution events so they can go in-line before the execution
         * 
        /// <summary>
        /// Gets the stored return value and assigns it somewhere
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="assigner"></param>
        /// <returns></returns>
        public FluidSql AssignReturnValue<T>(Action<T> assigner) {
            assigner(GetReturnValue<T>());
            return this;
        }

        public FluidSql AssignRecordsAffected(Action<int> assigner) {
            assigner(RecordsAffected);
            return this;
        }

        public FluidSql AssignCommandExecutionCount(Action<int> assigner) {
            assigner(CommandExecutionCount);
            return this;
        }
        */
        #endregion

        #region Internal Setup
        protected void PrepareCommand() {

            if (Parameters.Count > 0)
                Parameters.ToList().ForEach(p => Command.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value));

            if (OutputParameters.Count > 0)
                AddOutputParameters();
        }

        protected void CreateCommand(string commandText, CommandType commandType, object parameters = null) {
            Command = new SqlCommand(commandText, Connection) {CommandType = commandType};
            AddParameters(parameters);

            if (commandType == CommandType.StoredProcedure)
                AddReturnValueParameter();
        }

        protected void AddReturnValueParameter() {
            ReturnValueParameter = Command.Parameters.Add("@RETURN_VALUE", SqlDbType.Variant);
            ReturnValueParameter.Direction = ParameterDirection.ReturnValue;
        }

        protected void AddOutputParameters() {
            OutputParameters.Action(p => Command.Parameters.Add(p));
        }
        #endregion

        #region Internal execution
        protected int InternalExecuteNonQuery() {
            return (RecordsAffected = InternalExecute(Command.ExecuteNonQuery, true));
        }

        protected SqlDataReader InternalExecuteReader(CommandBehavior commandBehavior = CommandBehavior.Default) {
            return InternalExecute(() => Command.ExecuteReader(commandBehavior));
        }

        protected object InternalExecuteScalar() {
            return InternalExecute(Command.ExecuteScalar, true);
        }

        protected T InternalExecute<T>(Func<T> executor, bool dataReadComplete = false) {
            OnExecutingCommand();

            try {
                ExecutionTimer.Start();
                var result = executor();
                ExecutionTimer.Stop();

                return result;
            }
            catch (SqlException ex) {
                if (ErrorHandlers.Count == 0)
                    throw;

                foreach (var h in ErrorHandlers)
                    h.Handler(this, ex);
            }
            finally {
                OnExecutedCommand(dataReadComplete);
            }

            return default(T);
        }

        protected void OpenConnection() {

            if (Connection.State == ConnectionState.Open)
                return;

            try {
                Connection.Open();
            }
            catch (SqlException ex) {
                if (ErrorHandlers.Count == 0)
                    throw;

                foreach (var h in ErrorHandlers)
                    h.Handler(this, ex);
            }
        }
        #endregion

        #region Internal Events
        protected void OnExecutingCommand() {

            if (ConnectionStateChangeHandlers.Count > 0)
                ConnectionStateChangeHandlers.ToList()
                    .ForEach(h => Connection.StateChange += (sender, e) => h.Handler(this, e));

            if (InfoHandlers.Count > 0)
                InfoHandlers.ToList()
                    .ForEach(h => Connection.InfoMessage += (sender, e) => h.Handler(this, e));

            OpenConnection();
            PrepareCommand();
        }

        protected void OnExecutedCommand(bool dataReadComplete = false) {
            CommandExecutionCount++;

            if (Executed != null)
                Executed(this, new CommandExecutedEventArgs(ExecutionTimer.Elapsed, Command));

            if (dataReadComplete)
                OnDataRead();
        }

        protected void OnDataRead() {
            if (AutoClose && Connection.State != ConnectionState.Closed)
                Connection.Close();
        }

        protected void OnConnectionError() {
            
        }

        protected void OnCommandError() {
            
        }
        #endregion
    }
}
