﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.PersistentJobQueue;
using Hangfire.States;
using Hangfire.Storage;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Hangfire.Mongo
{
#pragma warning disable 1591

    public sealed class MongoWriteOnlyTransaction : JobStorageTransaction
    {
        private readonly HangfireDbContext _connection;

        private readonly PersistentJobQueueProviderCollection _queueProviders;

        private readonly IList<WriteModel<BsonDocument>> _writeModels = new List<WriteModel<BsonDocument>>();

        private readonly IList<Tuple<string, string>> _jobsToEnqueue = new List<Tuple<string, string>>();
        
        public MongoWriteOnlyTransaction(HangfireDbContext connection,
            PersistentJobQueueProviderCollection queueProviders)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _queueProviders = queueProviders ?? throw new ArgumentNullException(nameof(queueProviders));
        }

        public override void Dispose()
        {
        }

        public override void ExpireJob(string jobId, TimeSpan expireIn)
        {
            var filter = CreateJobIdFilter(jobId);
            var update = new BsonDocument("$set", new BsonDocument(nameof(KeyJobDto.ExpireAt), DateTime.UtcNow.Add(expireIn))); 
                
            var writeModel = new UpdateOneModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void PersistJob(string jobId)
        {
            var filter = CreateJobIdFilter(jobId);
            var update = new BsonDocument("$set", new BsonDocument(nameof(KeyJobDto.ExpireAt), BsonNull.Value));
            
            var writeModel = new UpdateOneModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void SetJobState(string jobId, IState state)
        {
            var filter = CreateJobIdFilter(jobId);
            var stateDto = new StateDto
            {
                Name = state.Name,
                Reason = state.Reason,
                CreatedAt = DateTime.UtcNow,
                Data = state.SerializeData()
            }.ToBsonDocument();

            var update = new BsonDocument
            {
                ["$set"] = new BsonDocument(nameof(JobDto.StateName), state.Name),
                ["$push"] = new BsonDocument(nameof(JobDto.StateHistory), stateDto)
            };
            var writeModel = new UpdateOneModel<BsonDocument>(filter, update);
            
            _writeModels.Add(writeModel);
        }

        public override void AddJobState(string jobId, IState state)
        {
            var filter = CreateJobIdFilter(jobId);
            var stateDto = new StateDto
            {
                Name = state.Name,
                Reason = state.Reason,
                CreatedAt = DateTime.UtcNow,
                Data = state.SerializeData()
            }.ToBsonDocument();

            var update = new BsonDocument("$push", new BsonDocument(nameof(JobDto.StateHistory), stateDto));
            
            var writeModel = new UpdateOneModel<BsonDocument>(filter, update);
            
            _writeModels.Add(writeModel);
        }

        public override void AddToQueue(string queue, string jobId)
        {
            _jobsToEnqueue.Add(Tuple.Create(queue, jobId));
        }

        public override void IncrementCounter(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var counterDto = new CounterDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = key,
                Value = +1L
            };
            var writeModel = new InsertOneModel<BsonDocument>(counterDto.ToBsonDocument());
            _writeModels.Add(writeModel);
        }

        public override void IncrementCounter(string key, TimeSpan expireIn)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var counterDto = new CounterDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = key,
                Value = +1L,
                ExpireAt = DateTime.UtcNow.Add(expireIn)
            };
            var writeModel = new InsertOneModel<BsonDocument>(counterDto.ToBsonDocument());
            _writeModels.Add(writeModel);
        }

        public override void DecrementCounter(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var counterDto = new CounterDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = key,
                Value = -1L
            };
            var writeModel = new InsertOneModel<BsonDocument>(counterDto.ToBsonDocument());
            _writeModels.Add(writeModel);
        }

        public override void DecrementCounter(string key, TimeSpan expireIn)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var counterDto = new CounterDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = key,
                Value = -1L,
                ExpireAt = DateTime.UtcNow.Add(expireIn)
            };
            var writeModel = new InsertOneModel<BsonDocument>(counterDto.ToBsonDocument());
            _writeModels.Add(writeModel);
        }

        public override void AddToSet(string key, string value)
        {
            AddToSet(key, value, 0.0);
        }

        public override void AddToSet(string key, string value, double score)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            
            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(SetDto.Key), key),
                new BsonDocument(nameof(SetDto.Value), value),
                new BsonDocument("_t", nameof(SetDto)),
            });
            var update = new BsonDocument
            {
                ["$set"] = new BsonDocument(nameof(SetDto.Score), score),
                ["$setOnInsert"] = new BsonDocument
                {
                    ["_t"] = new BsonArray {nameof(BaseJobDto), nameof(KeyJobDto), nameof(SetDto)},
                    [nameof(KeyJobDto.ExpireAt)] = BsonNull.Value
                }
            };
                
            var writeModel = new UpdateOneModel<BsonDocument>(filter, update.ToBsonDocument()){IsUpsert = true};
            _writeModels.Add(writeModel);
        }

        public override void RemoveFromSet(string key, string value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument(nameof(SetDto.Value), value),
                new BsonDocument("_t", nameof(SetDto))
            });

            var writeModel = new DeleteManyModel<BsonDocument>(filter);
            _writeModels.Add(writeModel);
        }

        public override void InsertToList(string key, string value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var listDto = new ListDto
            {
                Id = ObjectId.GenerateNewId(),
                Key = key,
                Value = value
            };
            
            var writeModel = new InsertOneModel<BsonDocument>(listDto.ToBsonDocument());
            _writeModels.Add(writeModel);
        }

        public override void RemoveFromList(string key, string value)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument(nameof(ListDto.Value), value),
                new BsonDocument("_t", nameof(ListDto))
            });

            var writeModel = new DeleteManyModel<BsonDocument>(filter);
            _writeModels.Add(writeModel);
        }

        public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            
            var start = keepStartingFrom + 1;
            var end = keepEndingAt + 1;

            var listIds = ((IEnumerable<ListDto>)_connection.JobGraph.OfType<ListDto>()
                    .Find(new BsonDocument())
                    .Project(Builders<ListDto>.Projection.Include(_ => _.Key))
                    .Project(_ => _)
                    .ToList())
                .Reverse()
                .Select((data, i) => new { Index = i + 1, Data = data.Id })
                .Where(_ => ((_.Index >= start) && (_.Index <= end)) == false)
                .Select(_ => _.Data);
            
            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("$in", new BsonArray(listIds)),
                new BsonDocument("_t", nameof(ListDto))
            });

            var writeModel = new DeleteManyModel<BsonDocument>(filter);
            _writeModels.Add(writeModel);
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            if (keyValuePairs == null)
            {
                throw new ArgumentNullException(nameof(keyValuePairs));
            }

            foreach (var keyValuePair in keyValuePairs)
            {
                var field = keyValuePair.Key;
                var value = keyValuePair.Value;
                var filter = new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument(nameof(HashDto.Key), key),
                    new BsonDocument(nameof(HashDto.Field), field),
                    new BsonDocument("_t", nameof(HashDto))
                });
                
                var update = new BsonDocument
                {
                    ["$set"] = new BsonDocument(nameof(HashDto.Value), value),
                    ["$setOnInsert"] = new BsonDocument
                    {
                        ["_t"] = new BsonArray {nameof(BaseJobDto), nameof(KeyJobDto), nameof(HashDto)},
                        [nameof(HashDto.ExpireAt)] = BsonNull.Value
                    }
                };

                var writeModel = new UpdateManyModel<BsonDocument>(filter, update.ToBsonDocument());
                _writeModels.Add(writeModel);
            }
        }

        public override void RemoveHash(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument(nameof(HashDto.Key), key);
            var writeModel = new DeleteManyModel<BsonDocument>(filter);
            _writeModels.Add(writeModel);
        }

        public override void Commit()
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            foreach (var tuple in _jobsToEnqueue)
            {
                Console.WriteLine($"Enqueuing Job ({tuple.Item2}), on queue: '{tuple.Item1}'\r\n");
            }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\r\nCommit:\r\n {string.Join("\r\n", _writeModels.Select(SerializeWriteModel))}");
            Console.ResetColor();
            
            var writeTask = _connection
                .Database
                .GetCollection<BsonDocument>(_connection.JobGraph.CollectionNamespace.CollectionName)
                .BulkWriteAsync(_writeModels);

            var writeTasks = _jobsToEnqueue.Select(t =>
            {
                var queue = t.Item1;
                var jobId = t.Item2;
                IPersistentJobQueueProvider provider = _queueProviders.GetProvider(queue);
                IPersistentJobQueue persistentQueue = provider.GetJobQueue(_connection);
                return Task.Run(() => persistentQueue.Enqueue(queue, jobId));
            }).ToList();
            
            writeTasks.Add(writeTask);
            
            // make sure to run tasks on default task scheduler
            Task.Run(() => Task.WhenAll(writeTasks)).GetAwaiter().GetResult();
        }

        private string SerializeWriteModel(WriteModel<BsonDocument> writeModel)
        {
            string serializedDoc;
            
            var serializer = _connection
                .Database
                .GetCollection<BsonDocument>(_connection.JobGraph.CollectionNamespace.CollectionName)
                .DocumentSerializer;
            
            var registry = _connection.JobGraph.Settings.SerializerRegistry;

            var jsonSettings = new JsonWriterSettings
            {
                Indent = true
            };
            switch (writeModel.ModelType)
            {
                case WriteModelType.InsertOne:
                    serializedDoc = ((InsertOneModel<BsonDocument>) writeModel).Document.ToJson(jsonSettings);
                    break;
                case WriteModelType.DeleteOne:
                    serializedDoc = ((DeleteOneModel<BsonDocument>) writeModel).Filter.Render(serializer, registry)
                        .ToJson(jsonSettings);
                    break;    
                case WriteModelType.DeleteMany:
                    serializedDoc = ((DeleteManyModel<BsonDocument>) writeModel).Filter.Render(serializer, registry)
                        .ToJson(jsonSettings);
                    break;
                case WriteModelType.ReplaceOne:
                    
                    serializedDoc = new Dictionary<string, BsonDocument>
                        {
                            ["Filter"] = ((ReplaceOneModel<BsonDocument>) writeModel).Filter.Render(serializer, registry),
                            ["Replacement"] = ((ReplaceOneModel<BsonDocument>) writeModel).Replacement
                        }.ToJson(jsonSettings);
                    break;
                case WriteModelType.UpdateOne:
                    serializedDoc = new Dictionary<string, BsonDocument>
                    {
                        ["Filter"] = ((UpdateOneModel<BsonDocument>) writeModel).Filter.Render(serializer, registry),
                        ["Update"] = ((UpdateOneModel<BsonDocument>) writeModel).Update.Render(serializer, registry)
                    }.ToJson(jsonSettings);
                    break;
                case WriteModelType.UpdateMany:
                    serializedDoc = new Dictionary<string, BsonDocument>
                    {
                        ["Filter"] = ((UpdateManyModel<BsonDocument>) writeModel).Filter.Render(serializer, registry),
                        ["Update"] = ((UpdateManyModel<BsonDocument>) writeModel).Update.Render(serializer, registry)
                    }.ToJson(jsonSettings);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            
            return $"{writeModel.ModelType}: {serializedDoc}";
        }
        // New methods to support Hangfire pro feature - batches.


        public override void ExpireSet(string key, TimeSpan expireIn)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(SetDto))
            });
                
            var update = new BsonDocument("$set",
                new BsonDocument(nameof(BaseJobDto.ExpireAt), DateTime.UtcNow.Add(expireIn)));
            
            var writeModel = new UpdateManyModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void ExpireList(string key, TimeSpan expireIn)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(ListDto))
            });
            
            var update = new BsonDocument("$set",
                new BsonDocument(nameof(BaseJobDto.ExpireAt), DateTime.UtcNow.Add(expireIn)));
            var writeModel = new UpdateManyModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void ExpireHash(string key, TimeSpan expireIn)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(HashDto))
            });
            
            var update = new BsonDocument("$set",
                new BsonDocument(nameof(BaseJobDto.ExpireAt), DateTime.UtcNow.Add(expireIn)));
            var writeModel = new UpdateManyModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        
        public override void PersistSet(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(SetDto))
            });
            
            var update = new BsonDocument("$set",
                new BsonDocument(nameof(BaseJobDto.ExpireAt), BsonNull.Value));
            
            var writeModel = new UpdateManyModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void PersistList(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(ListDto))
            });
            
            var update = new BsonDocument("$set",
                new BsonDocument(nameof(BaseJobDto.ExpireAt), BsonNull.Value));
            
            var writeModel = new UpdateManyModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void PersistHash(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(HashDto))
            });
            
            var update = new BsonDocument("$set",
                new BsonDocument(nameof(BaseJobDto.ExpireAt), BsonNull.Value));
            
            var writeModel = new UpdateManyModel<BsonDocument>(filter, update);
            _writeModels.Add(writeModel);
        }

        public override void AddRangeToSet(string key, IList<string> items)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }
            

            var update = new BsonDocument
            {
                ["$set"] = new BsonDocument(nameof(SetDto.Score), 0.0),
                ["$setOnInsert"] = new BsonDocument
                {
                    ["_t"] = new BsonArray {nameof(BaseJobDto), nameof(KeyJobDto), nameof(SetDto)},
                    [nameof(SetDto.ExpireAt)] = BsonNull.Value
                }
            };
            
            foreach (var item in items)
            {
                var filter = new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument(nameof(KeyJobDto.Key), key),
                    new BsonDocument(nameof(SetDto.Value), item),
                    new BsonDocument("_t", nameof(SetDto))
                });
                var writeModel = new UpdateManyModel<BsonDocument>(filter, update) {IsUpsert = true};
                _writeModels.Add(writeModel);
            }

        }

        public override void RemoveSet(string key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            var filter = new BsonDocument("$and", new BsonArray
            {
                new BsonDocument(nameof(KeyJobDto.Key), key),
                new BsonDocument("_t", nameof(SetDto))
            });
            
            var writeModel = new DeleteManyModel<BsonDocument>(filter);
            _writeModels.Add(writeModel);
        }

        private static BsonDocument CreateJobIdFilter(string jobId)
        {
            return new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("_id", ObjectId.Parse(jobId)),
                new BsonDocument("_t", nameof(JobDto))
            }); 
        }
    }

#pragma warning restore 1591
}