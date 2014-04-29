﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using Dapper;
using HangFire.States;
using Moq;
using Xunit;

namespace HangFire.SqlServer.Tests
{
    public class WriteTransactionFacts
    {
        [Fact]
        public void Ctor_ThrowsAnException_IfConnectionIsNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => new SqlServerWriteOnlyTransaction(null));
        }

        [Fact, CleanDatabase]
        public void ExpireJob_SetsJobExpirationData()
        {
            const string arrangeSql = @"
insert into HangFire.Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

            UseConnection(sql =>
            {
                var jobId = sql.Query(arrangeSql).Single().Id.ToString();
                var anotherJobId = sql.Query(arrangeSql).Single().Id.ToString();

                Commit(sql, x => x.ExpireJob(jobId, TimeSpan.FromDays(1)));

                var job = GetTestJob(sql, jobId);
                Assert.True(DateTime.UtcNow < job.ExpireAt && job.ExpireAt < DateTime.UtcNow.AddDays(1));

                var anotherJob = GetTestJob(sql, anotherJobId);
                Assert.Null(anotherJob.ExpireAt);
            });
        }

        [Fact, CleanDatabase]
        public void PersistJob_ClearsTheJobExpirationData()
        {
            const string arrangeSql = @"
insert into HangFire.Job (InvocationData, Arguments, CreatedAt, ExpireAt)
values ('', '', getutcdate(), getutcdate())
select scope_identity() as Id";

            UseConnection(sql =>
            {
                var jobId = sql.Query(arrangeSql).Single().Id.ToString();
                var anotherJobId = sql.Query(arrangeSql).Single().Id.ToString();

                Commit(sql, x => x.PersistJob(jobId));

                var job = GetTestJob(sql, jobId);
                Assert.Null(job.ExpireAt);

                var anotherJob = GetTestJob(sql, anotherJobId);
                Assert.NotNull(anotherJob.ExpireAt);
            });
        }

        [Fact, CleanDatabase]
        public void SetJobState_AppendsAStateAndSetItToTheJob()
        {
            const string arrangeSql = @"
insert into HangFire.Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

            UseConnection(sql =>
            {
                var jobId = sql.Query(arrangeSql).Single().Id.ToString();
                var anotherJobId = sql.Query(arrangeSql).Single().Id.ToString();

                var state = new Mock<State>();
                state.Setup(x => x.Name).Returns("State");
                state.Setup(x => x.SerializeData())
                    .Returns(new Dictionary<string, string> { { "Name", "Value" } });
                state.Object.Reason = "Reason";

                Commit(sql, x => x.SetJobState(jobId, state.Object));

                var job = GetTestJob(sql, jobId);
                Assert.Equal("State", job.StateName);
                Assert.NotNull(job.StateId);

                var anotherJob = GetTestJob(sql, anotherJobId);
                Assert.Null(anotherJob.StateName);
                Assert.Null(anotherJob.StateId);

                var jobState = sql.Query("select * from HangFire.State").Single();
                Assert.Equal((string)jobId, jobState.JobId.ToString());
                Assert.Equal("State", jobState.Name);
                Assert.Equal("Reason", jobState.Reason);
                Assert.NotNull(jobState.CreatedAt);
                Assert.Equal("{\"Name\":\"Value\"}", jobState.Data);
            });
        }

        [Fact, CleanDatabase]
        public void AddJobState_JustAddsANewRecordInATable()
        {
            const string arrangeSql = @"
insert into HangFire.Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

            UseConnection(sql =>
            {
                var jobId = sql.Query(arrangeSql).Single().Id.ToString();

                var state = new Mock<State>();
                state.Setup(x => x.Name).Returns("State");
                state.Setup(x => x.SerializeData())
                    .Returns(new Dictionary<string, string> { { "Name", "Value" } });
                state.Object.Reason = "Reason";

                Commit(sql, x => x.AddJobState(jobId, state.Object));

                var job = GetTestJob(sql, jobId);
                Assert.Null(job.StateName);
                Assert.Null(job.StateId);

                var jobState = sql.Query("select * from HangFire.State").Single();
                Assert.Equal((string)jobId, jobState.JobId.ToString());
                Assert.Equal("State", jobState.Name);
                Assert.Equal("Reason", jobState.Reason);
                Assert.NotNull(jobState.CreatedAt);
                Assert.Equal("{\"Name\":\"Value\"}", jobState.Data);
            });
        }

        [Fact, CleanDatabase]
        public void AddToQueue_AddsAJobToTheQueue()
        {
            UseConnection(sql =>
            {
                Commit(sql, x => x.AddToQueue("default", "1"));

                var record = sql.Query("select * from HangFire.JobQueue").Single();
                Assert.Equal("1", record.JobId.ToString());
                Assert.Equal("default", record.Queue);
                Assert.Null(record.FetchedAt);
            });
        }

        private static dynamic GetTestJob(IDbConnection connection, string jobId)
        {
            return connection
                .Query("select * from HangFire.Job where id = @id", new { id = jobId })
                .Single();
        }

        [Fact, CleanDatabase]
        public void IncrementCounter_AddsRecordToCounterTable_WithPositiveValue()
        {
            UseConnection(sql =>
            {
                Commit(sql, x => x.IncrementCounter("my-key"));

                var record = sql.Query("select * from HangFire.Counter").Single();
                
                Assert.Equal("my-key", record.Key);
                Assert.Equal(1, record.Value);
            });
        }

        [Fact, CleanDatabase]
        public void IncrementCounter_WithExistingKey_AddsAnotherRecord()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.IncrementCounter("my-key");
                    x.IncrementCounter("my-key");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.Counter").Single();
                
                Assert.Equal(2, recordCount);
            });
        }

        [Fact, CleanDatabase]
        public void DecrementCounter_AddsRecordToCounterTable_WithNegativeValue()
        {
            UseConnection(sql =>
            {
                Commit(sql, x => x.DecrementCounter("my-key"));

                var record = sql.Query("select * from HangFire.Counter").Single();

                Assert.Equal("my-key", record.Key);
                Assert.Equal(-1, record.Value);
            });
        }

        [Fact, CleanDatabase]
        public void DecrementCounter_WithExistingKey_AddsAnotherRecord()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.DecrementCounter("my-key");
                    x.DecrementCounter("my-key");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.Counter").Single();

                Assert.Equal(2, recordCount);
            });
        }

        [Fact, CleanDatabase]
        public void AddToSet_AddsARecord_IfThereIsNo_SuchKeyAndValue()
        {
            UseConnection(sql =>
            {
                Commit(sql, x => x.AddToSet("my-key", "my-value"));

                var record = sql.Query("select * from HangFire.[Set]").Single();

                Assert.Equal("my-key", record.Key);
                Assert.Equal("my-value", record.Value);
                Assert.Equal(0.0, record.Score, 2);
            });
        }

        [Fact, CleanDatabase]
        public void AddToSet_AddsARecord_WhenKeyIsExists_ButValuesAreDifferent()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.AddToSet("my-key", "my-value");
                    x.AddToSet("my-key", "another-value");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.[Set]").Single();

                Assert.Equal(2, recordCount);
            });
        }

        [Fact, CleanDatabase]
        public void AddToSet_DoesNotAddARecord_WhenBothKeyAndValueAreExist()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.AddToSet("my-key", "my-value");
                    x.AddToSet("my-key", "my-value");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.[Set]").Single();
                
                Assert.Equal(1, recordCount);
            });
        }

        [Fact, CleanDatabase]
        public void AddToSet_WithScore_AddsARecordWithScore_WhenBothKeyAndValueAreNotExist()
        {
            UseConnection(sql =>
            {
                Commit(sql, x => x.AddToSet("my-key", "my-value", 3.2));

                var record = sql.Query("select * from HangFire.[Set]").Single();

                Assert.Equal("my-key", record.Key);
                Assert.Equal("my-value", record.Value);
                Assert.Equal(3.2, record.Score, 3);
            });
        }

        [Fact, CleanDatabase]
        public void AddToSet_WithScore_UpdatesAScore_WhenBothKeyAndValueAreExist()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.AddToSet("my-key", "my-value");
                    x.AddToSet("my-key", "my-value", 3.2);
                });

                var record = sql.Query("select * from HangFire.[Set]").Single();

                Assert.Equal(3.2, record.Score, 3);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveFromSet_RemovesARecord_WithGivenKeyAndValue()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.AddToSet("my-key", "my-value");
                    x.RemoveFromSet("my-key", "my-value");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.[Set]").Single();

                Assert.Equal(0, recordCount);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveFromSet_DoesNotRemoveRecord_WithSameKey_AndDifferentValue()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.AddToSet("my-key", "my-value");
                    x.RemoveFromSet("my-key", "different-value");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.[Set]").Single();

                Assert.Equal(1, recordCount);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveFromSet_DoesNotRemoveRecord_WithSameValue_AndDifferentKey()
        {
            UseConnection(sql =>
            {
                Commit(sql, x =>
                {
                    x.AddToSet("my-key", "my-value");
                    x.RemoveFromSet("different-key", "my-value");
                });

                var recordCount = sql.Query<int>("select count(*) from HangFire.[Set]").Single();

                Assert.Equal(1, recordCount);
            });
        }

        private void UseConnection(Action<SqlConnection> action)
        {
            using (var connection = ConnectionUtils.CreateConnection())
            {
                action(connection);
            }
        }

        private void Commit(
            SqlConnection connection,
            Action<SqlServerWriteOnlyTransaction> action)
        {
            using (var transaction = new SqlServerWriteOnlyTransaction(connection))
            {
                action(transaction);
                transaction.Commit();
            }
        }
    }
}
