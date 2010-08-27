﻿/* Copyright 2010 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using MongoDB.BsonLibrary;
using MongoDB.MongoDBClient.Internal;

namespace MongoDB.MongoDBClient {
    public class MongoDatabase {
        #region private fields
        private MongoServer server;
        private string name;
        private MongoCredentials credentials;
        private bool safeMode;
        private bool useDedicatedConnection;
        private MongoConnection dedicatedConnection;
        private Dictionary<string, MongoCollection> collections = new Dictionary<string, MongoCollection>();
        #endregion

        #region constructors
        public MongoDatabase(
            MongoServer server,
            string name
        ) {
            ValidateDatabaseName(name);
            this.server = server;
            this.name = name;
            this.safeMode = server.SafeMode;
        }

        public MongoDatabase(
            MongoServer server,
            string name,
            MongoCredentials credentials
        ) {
            ValidateDatabaseName(name);
            this.server = server;
            this.name = name;
            this.credentials = credentials;
            this.safeMode = server.SafeMode;
        }
        #endregion

        #region factory methods
        public static MongoDatabase FromConnectionString(
            string connectionString
        ) {
            if (connectionString.StartsWith("mongodb://")) {
                MongoUrl url = new MongoUrl(connectionString);
                return FromMongoUrl(url);
            } else {
                MongoConnectionStringBuilder builder = new MongoConnectionStringBuilder(connectionString);
                return FromMongoConnectionStringBuilder(builder);
            }
        }

        internal static MongoDatabase FromMongoConnectionSettings(
            IMongoConnectionSettings settings
        ) {
            if (settings.Database == null) {
                throw new ArgumentException("Connection string must have database name");
            }
            MongoServer server = MongoServer.FromMongoConnectionSettings(settings);
            return server.GetDatabase(settings.Database);
        }

        public static MongoDatabase FromMongoConnectionStringBuilder(
            MongoConnectionStringBuilder builder
        ) {
            return FromMongoConnectionSettings(builder);
        }

        public static MongoDatabase FromMongoUrl(
            MongoUrl url
        ) {
            return FromMongoConnectionSettings(url);
        }

        public static MongoDatabase FromUri(
            Uri uri
        ) {
            return FromMongoUrl(new MongoUrl(uri.ToString()));
        }
        #endregion

        #region public properties
        public MongoCollection CommandCollection {
            get { return GetCollection("$cmd"); }
        }

        public MongoCredentials Credentials {
            get { return credentials; }
        }

        public string Name {
            get { return name; }
        }

        public bool SafeMode {
            get { return safeMode; }
            set { safeMode = value; }
        }

        public MongoServer Server {
            get { return server; }
        }

        public bool UseDedicatedConnection {
            get { return useDedicatedConnection; }
            set {
                useDedicatedConnection = value;
                if (!useDedicatedConnection) { ReleaseDedicatedConnection(); }
            }
        }
        #endregion

        #region public indexers
        public MongoCollection this[
            string collectionName
        ] {
            get { return GetCollection(collectionName); }
        }
        #endregion

        #region public methods
        public void AddUser(
            MongoCredentials credentials
        ) {
            var users = GetCollection("system.users");
            var user = users.FindOne<BsonDocument>(new BsonDocument("user", credentials.Username));
            if (user == null) {
                user = new BsonDocument("user", credentials.Username);
            }
            user["pwd"] = Mongo.Hash(credentials.Username + ":mongo:" + credentials.Password);
            users.Save(user);
        }

        public bool CollectionExists(
            string collectionName
        ) {
            return GetCollectionNames().Contains(collectionName);
        }

        public MongoCollection CreateCollection(
            string collectionName,
            BsonDocument options
        ) {
            if (options != null) {
                BsonDocument command = new BsonDocument("create", collectionName);
                command.Add(options);
                RunCommand(command);
            }
            return GetCollection(collectionName);
        }

        public BsonDocument CurrentOp() {
            var collection = GetCollection("$cmd.sys.inprog");
            return collection.FindOne<BsonDocument>();
        }
           
        public void DropCollection(
            string collectionName
        ) {
            BsonDocument command = new BsonDocument("drop", collectionName);
            RunCommand(command);
        }

        public object Eval(
            string code,
            params object[] args
        ) {
            BsonDocument command = new BsonDocument {
                { "$eval", code },
                { "args", args }
            };
            var result = RunCommand(command);
            return result["retval"];
        }

        public MongoCollection GetCollection(
           string collectionName
        ) {
            MongoCollection collection;
            if (!collections.TryGetValue(collectionName, out collection)) {
                collection = new MongoCollection(this, collectionName);
                collections[collectionName] = collection;
            }
            return collection;
        }

        public MongoCollection<T> GetCollection<T>(
            string collectionName
        ) where T : new() {
            MongoCollection collection;
            string key = string.Format("{0}<{1}>", collectionName, typeof(T).FullName);
            if (!collections.TryGetValue(key, out collection)) {
                collection = new MongoCollection<T>(this, collectionName);
                collections[collectionName] = collection;
            }
            return (MongoCollection<T>) collection;
        }

        public List<string> GetCollectionNames() {
            List<string> collectionNames = new List<string>();
            MongoCollection namespaces = GetCollection("system.namespaces");
            var prefix = name + ".";
            foreach (BsonDocument ns in namespaces.FindAll<BsonDocument>()) {
                string collectionName = (string) ns["name"];
                if (!collectionName.StartsWith(prefix)) { continue; }
                if (collectionName.Contains('$')) { continue; }
                collectionNames.Add(collectionName);
            }
            collectionNames.Sort();
            return collectionNames;
        }

        // requires UseDedicatedConnection == true to return accurate results
        public BsonDocument GetLastError() {
            if (!useDedicatedConnection) {
                throw new MongoException("GetLastError can only be called if UseDedicatedConnection is true");
            }
            return RunCommand("getLastError");
        }

        // TODO: mongo shell has GetPrevError at the database level?
        // TODO: mongo shell has GetProfilingLevel at the database level?
        // TODO: mongo shell has GetReplicationInfo at the database level?

        public MongoDatabase GetSisterDatabase(
            string databaseName
        ) {
            return server.GetDatabase(databaseName);
        }

        public BsonDocument GetStats() {
            return RunCommand("dbstats");
        }

        // TODO: mongo shell has IsMaster at database level?

        public void ReleaseDedicatedConnection() {
            if (dedicatedConnection != null) {
                MongoConnectionPool.ReleaseConnection(dedicatedConnection);
                dedicatedConnection = null;
            }
        }

        public void RemoveUser(
            string username
        ) {
            MongoCollection users = GetCollection("system.users");
            users.Remove(new BsonDocument("user", username));
        }

        // TODO: mongo shell has ResetError at the database level

        public BsonDocument RunCommand(
            BsonDocument command
        ) {
            BsonDocument result = CommandCollection.FindOne<BsonDocument>(command);

            object ok = result["ok"];
            if (ok == null) {
                throw new MongoException("ok element is missing");
            }
            if (
                ok is bool && !((bool) ok) ||
                ok is int && (int) ok != 1 ||
                ok is double && (double) ok != 1.0
            ) {
                string commandName = (string) command.GetElement(0).Name;
                string errmsg = (string) result["errmsg"];
                string errorMessage = string.Format("{0} failed ({1})", commandName, errmsg);
                throw new MongoException(errorMessage);
            }

            return result;
        }

        public BsonDocument RunCommand(
            string commandName
        ) {
            BsonDocument command = new BsonDocument(commandName, true);
            return RunCommand(command);
        }

        // TODO: 

        public override string ToString() {
            return name;
        }
        #endregion

        #region internal methods
        internal MongoConnection AcquireConnection() {
            if (useDedicatedConnection) {
                if (dedicatedConnection == null) {
                    dedicatedConnection = MongoConnectionPool.AcquireConnection(this);
                }
                return dedicatedConnection;
            } else {
                return MongoConnectionPool.AcquireConnection(this);
            }
        }

        internal void ReleaseConnection(
            MongoConnection connection
        ) {
            if (!useDedicatedConnection) {
                MongoConnectionPool.ReleaseConnection(connection);
            }
        }
        #endregion

        #region private methods
        private void ValidateDatabaseName(
            string name
        ) {
            if (name == null) {
                throw new ArgumentNullException("name");
            }
            if (
                name == "" ||
                name.IndexOfAny(new char[] { '\0', ' ', '.', '$', '/', '\\' }) != -1 ||
                name != name.ToLower() ||
                Encoding.UTF8.GetBytes(name).Length > 64
            ) {
                throw new MongoException("Invalid database name");
            }
        }
        #endregion
    }
}