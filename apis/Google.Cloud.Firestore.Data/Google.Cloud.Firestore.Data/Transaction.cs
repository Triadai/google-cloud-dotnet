﻿// Copyright 2017, Google Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.using System;


using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore.V1Beta1;
using Google.Protobuf;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static Google.Cloud.Firestore.V1Beta1.TransactionOptions.Types;

namespace Google.Cloud.Firestore.Data
{
    /// <summary>
    /// A transaction, as created by
    /// <see cref="FirestoreDb.RunTransactionAsync{T}(System.Func{Transaction, Task{T}}, TransactionOptions, CancellationToken)"/>
    /// (and overloads) and passed to user code.
    /// </summary>
    public class Transaction
    {
        /// <summary>
        /// The cancellation token for this transaction
        /// </summary>
        public CancellationToken CancellationToken { get; }

        /// <summary>
        /// The database for this transaction.
        /// </summary>
        public FirestoreDb Database { get; }

        private readonly WriteBatch _writes;

        internal ByteString TransactionId { get; }

        internal Transaction(FirestoreDb db, ByteString transactionId, CancellationToken overallCancellationToken)
        {
            Database = db;
            TransactionId = transactionId;
            CancellationToken = overallCancellationToken;
            _writes = new WriteBatch(db);
        }

        internal static async Task<Transaction> BeginAsync(FirestoreDb db, ByteString previousTransactionId, CancellationToken cancellationToken)
        {
            var request = new BeginTransactionRequest
            {
                Database = db.RootPath,
                Options = previousTransactionId == null ? null : new V1Beta1.TransactionOptions { ReadWrite = new ReadWrite { RetryTransaction = previousTransactionId } }
            };
            var response = await db.Client.BeginTransactionAsync(request, CallSettings.FromCancellationToken(cancellationToken)).ConfigureAwait(false);
            return new Transaction(db, response.Transaction, cancellationToken);
        }

        // TODO: Consider names of GetDocumentSnapshotAsync/GetQuerySnapshotAsync.
        // Perhaps just SnapshotDocumentAsync and SnapshotQueryAsync?
        // Just using SnapshotAsync overloads feels like a bad idea as they do quite different things.

        /// <summary>
        /// Fetch a snapshot of the document specified by <paramref name="documentReference"/>, with respect to this transaction.
        /// This method cannot be called after any write operations have been created.
        /// </summary>
        /// <param name="documentReference">The document reference to fetch. Must not be null.</param>
        /// <param name="cancellationToken">A cancellation token to monitor for the asynchronous operation.</param>
        /// <returns>A snapshot of the given document with respect to this transaction.</returns>
        public Task<DocumentSnapshot> GetDocumentSnapshotAsync(DocumentReference documentReference, CancellationToken cancellationToken = default)
        {
            GaxPreconditions.CheckNotNull(documentReference, nameof(documentReference));
            GaxPreconditions.CheckState(_writes.IsEmpty, "Firestore transactions require all reads to be executed before all writes.");
            CancellationToken effectiveToken = GetEffectiveCancellationToken(cancellationToken);
            return documentReference.SnapshotAsync(TransactionId, effectiveToken);
        }

        /// <summary>
        /// Performs a query and returned a snapshot of the the results, with respect to this transaction.
        /// This method cannot be called after any write operations have been created.
        /// </summary>
        /// <param name="query">The query to execute. Must not be null.</param>
        /// <param name="cancellationToken">A cancellation token to monitor for the asynchronous operation.</param>
        /// <returns>A snapshot of results of the given query with respect to this transaction.</returns>
        public Task<QuerySnapshot> GetQuerySnapshotAsync(Query query, CancellationToken cancellationToken = default)
        {
            GaxPreconditions.CheckNotNull(query, nameof(query));
            CancellationToken effectiveToken = GetEffectiveCancellationToken(cancellationToken);
            GaxPreconditions.CheckState(_writes.IsEmpty, "Firestore transactions require all reads to be executed before all writes.");
            return query.SnapshotAsync(TransactionId, cancellationToken);
        }

        /// <summary>
        /// Adds an operation to create a document in this transaction.
        /// </summary>
        /// <param name="documentReference">The document reference to create. Must not be null.</param>
        /// <param name="documentData">The data for the document. Must not be null.</param>
        public void Create(DocumentReference documentReference, object documentData)
        {
            // Preconditions are validated by WriteBatch.
            _writes.Create(documentReference, documentData);
        }

        /// <summary>
        /// Adds an operation to set a document's data in this transaction.
        /// </summary>
        /// <param name="documentReference">The document in which to set the data. Must not be null.</param>
        /// <param name="documentData">The data for the document. Must not be null.</param>
        /// <param name="options">The options to use when updating the document. May be null, which is equivalent to <see cref="SetOptions.Overwrite"/>.</param>
        public void Set(DocumentReference documentReference, object documentData, SetOptions options = null)
        {
            // Preconditions are validated by WriteBatch.
            _writes.Set(documentReference, documentData, options);
        }

        /// <summary>
        /// Adds an operation to update a document's data in this transaction.
        /// </summary>
        /// <param name="documentReference">The document to update. Must not be null.</param>
        /// <param name="updates">The updates to perform on the document, keyed by the field path to update. Fields not present in this dictionary are not updated. Must not be null.</param>
        /// <param name="precondition">Optional precondition for updating the document. May be null, which is equivalent to <see cref="Precondition.MustExist"/>.</param>
        public void Update(DocumentReference documentReference, Dictionary<FieldPath, object> updates, Precondition precondition = null)
        {
            // Preconditions are validated by WriteBatch.
            _writes.Update(documentReference, updates, precondition);
        }

        /// <summary>
        /// Adds an operation to delete a document's data in this transaction.
        /// </summary>
        /// <param name="documentReference">The document to delete. Must not be null.</param>
        /// <param name="precondition">Optional precondition for deletion. May be null, in which case the deletion is unconditional.</param>
        public void Delete(DocumentReference documentReference, Precondition precondition = null)
        {
            // Preconditions are validated by WriteBatch.
            _writes.Delete(documentReference, precondition);
        }

        // TODO: The specification has some rules around retrying the commit operation. Rather than implement them
        // here, it would be good to hook into the GAX retry mechanism - we don't want to build too many layers of
        // retry (each with backoff) on top of each other.

        /// <summary>
        /// Asynchronously commits the transaction, using the same cancellation token as was used to begin the transaction.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        internal Task CommitAsync() => _writes.CommitAsync(TransactionId, CancellationToken);

        /// <summary>
        /// Asynchronously rolls back the transaction, using the same cancellation token as was used to begin the transaction.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        internal Task RollbackAsync() =>
            Database.Client.RollbackAsync(Database.RootPath, TransactionId, CancellationToken);

        private CancellationToken GetEffectiveCancellationToken(CancellationToken other) =>
            !CancellationToken.CanBeCanceled ? other
            : !other.CanBeCanceled ? CancellationToken
            : CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, other).Token;
    }
}
