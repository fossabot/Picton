﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.Queue.Protocol;
using Picton.Interfaces;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Wire;

namespace Picton.Managers
{
	public class QueueManager : IQueueManager
	{
		#region FIELDS

		private readonly IStorageAccount _storageAccount;
		private readonly string _queueName;
		private readonly CloudQueue _queue;
		private readonly CloudBlobContainer _blobContainer;
		private static readonly long MAX_MESSAGE_CONTENT_SIZE = (CloudQueueMessage.MaxMessageSize - 1) / 4 * 3;
		private const string DateFormatInBlobName = "yyyy-MM-dd-HH-mm-ss-ffff";

		#endregion

		#region CONSTRUCTORS

		[ExcludeFromCodeCoverage]
		/// <summary>
		/// </summary>
		/// <param name="queueName"></param>
		/// <param name="cloudStorageAccount"></param>
		public QueueManager(string queueName, CloudStorageAccount cloudStorageAccount) :
			this(queueName, StorageAccount.FromCloudStorageAccount(cloudStorageAccount))
		{
		}

		/// <summary>
		/// For unit testing
		/// </summary>
		/// <param name="queueName"></param>
		public QueueManager(string queueName, IStorageAccount storageAccount)
		{
			if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentNullException(nameof(queueName));
			if (storageAccount == null) throw new ArgumentNullException(nameof(storageAccount));

			_storageAccount = storageAccount;
			_queueName = queueName;
			_queue = storageAccount.CreateCloudQueueClient().GetQueueReference(queueName);
			_blobContainer = storageAccount.CreateCloudBlobClient().GetContainerReference("oversizedqueuemessages");

			var tasks = new List<Task>();
			tasks.Add(_queue.CreateIfNotExistsAsync(null, null, CancellationToken.None));
			tasks.Add(_blobContainer.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Off, null, null, CancellationToken.None));
			Task.WaitAll(tasks.ToArray());
		}

		#endregion

		#region PUBLIC METHODS

		public async Task AddMessageAsync<T>(T message, TimeSpan? timeToLive = null, TimeSpan? initialVisibilityDelay = null, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			var serializer = new Serializer();
			var data = (byte[])null;
			using (var stream = new MemoryStream())
			{
				serializer.Serialize(message, stream);
				data = stream.ToArray();
			}

			// Check if the message exceeds the size allowed by Azure Storage queues
			if (data.Length > MAX_MESSAGE_CONTENT_SIZE)
			{
				// The message is too large. Therefore we must save the content to blob storage and
				// send a smaller message indicating where the actual message was saved

				// 1) Save the large message to blob storage
				var blobName = "abc123";
				var blob = _blobContainer.GetBlockBlobReference(blobName);
				await blob.UploadBytesAsync(data, null, cancellationToken).ConfigureAwait(false);

				// 2) Send a smaller message
				var largeEnvelope = new LargeMessageEnvelope
				{
					BlobName = blobName
				};
				using (var stream = new MemoryStream())
				{
					serializer.Serialize(largeEnvelope, stream);
					data = stream.ToArray();
				}
				var cloudMessage = new CloudQueueMessage(data);
				await _queue.AddMessageAsync(cloudMessage, timeToLive, initialVisibilityDelay, options, operationContext, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				// The size of this message is within the range allowed by Azure Storage queues
				var cloudMessage = new CloudQueueMessage(data);
				await _queue.AddMessageAsync(cloudMessage, timeToLive, initialVisibilityDelay, options, operationContext, cancellationToken).ConfigureAwait(false);
			}
		}

		public Task ClearAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.ClearAsync(options, operationContext, cancellationToken);
		}

		public Task CreateAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.CreateAsync(options, operationContext, cancellationToken);
		}

		public Task<bool> CreateIfNotExistsAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.CreateIfNotExistsAsync(options, operationContext, cancellationToken);
		}

		public Task<bool> DeleteIfExistsAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.DeleteIfExistsAsync(options, operationContext, cancellationToken);
		}

		public async Task DeleteMessageAsync(CloudMessage message, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (message.IsLargeMessage)
			{
				var blob = _blobContainer.GetBlobReference(message.LargeContentBlobName);
				await blob.DeleteAsync(cancellationToken);
			}
			await _queue.DeleteMessageAsync(message.Id, message.PopReceipt, options, operationContext, cancellationToken);
		}

		public Task<bool> ExistsAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.ExistsAsync(options, operationContext, cancellationToken);
		}

		public Task FetchAttributesAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.FetchAttributesAsync(options, operationContext, cancellationToken);
		}

		public async Task<CloudMessage> GetMessageAsync(TimeSpan? visibilityTimeout = null, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			// Get the next mesage from the queue
			var cloudMessage = await _queue.GetMessageAsync(visibilityTimeout, options, operationContext, cancellationToken).ConfigureAwait(false);

			// We get a null value when the queue is empty
			if (cloudMessage == null)
			{
				return null;
			}

			// Deserialize the content of the cloud message
			var content = (object)null;
			var largeContentBlobName = (string)null;
			try
			{
				content = Deserialize(cloudMessage.AsBytes);
			}
			catch
			{
				content = cloudMessage.AsString;
			}

			// If the serialized content exceeded the max Azure Storage size limit, it was saved in a blob
			if (content.GetType() == typeof(LargeMessageEnvelope))
			{
				var envelope = (LargeMessageEnvelope)content;
				var blob = _blobContainer.GetBlobReference(envelope.BlobName);

				using (var stream = new MemoryStream())
				{
					var buffer = (byte[])null;
					await blob.DownloadToByteArrayAsync(buffer, 0, null, null, null, cancellationToken).ConfigureAwait(false);
					content = Deserialize(buffer);
					largeContentBlobName = envelope.BlobName;
				}
			}

			var message = new CloudMessage(content)
			{
				DequeueCount = cloudMessage.DequeueCount,
				ExpirationTime = cloudMessage.ExpirationTime,
				Id = cloudMessage.Id,
				InsertionTime = cloudMessage.InsertionTime,
				LargeContentBlobName = largeContentBlobName,
				NextVisibleTime = cloudMessage.NextVisibleTime,
				PopReceipt = cloudMessage.PopReceipt
			};
			return message;
		}

		public Task<IEnumerable<CloudQueueMessage>> GetMessagesAsync(int messageCount, TimeSpan? visibilityTimeout = null, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (messageCount < 1) throw new ArgumentOutOfRangeException(nameof(messageCount), "must be greather than zero");
			if (messageCount > CloudQueueMessage.MaxNumberOfMessagesToPeek) throw new ArgumentOutOfRangeException(nameof(messageCount), "must be less than or equal to {CloudQueueMessage.MaxNumberOfMessagesToPeek}");

			return _queue.GetMessagesAsync(messageCount, visibilityTimeout, options, operationContext, cancellationToken);

			//var cloudMessages = await _queue.GetMessagesAsync(messageCount, visibilityTimeout, options, operationContext, cancellationToken);
			//var messages = cloudMessages.Select(cloudMessage => _serializer.Deserialize(cloudMessage.AsBytes) as IMessage);
			//return messages;
		}

		public Task<QueuePermissions> GetPermissionsAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.GetPermissionsAsync(options, operationContext, cancellationToken);
		}

		// GetSharedAccessSignature is not virtual therefore we can't mock it.
		[ExcludeFromCodeCoverage]
		public string GetSharedAccessSignature(SharedAccessQueuePolicy policy, string accessPolicyIdentifier, SharedAccessProtocol? protocols = null, IPAddressOrRange ipAddressOrRange = null)
		{
			return _queue.GetSharedAccessSignature(policy, accessPolicyIdentifier, protocols, ipAddressOrRange);
		}

		public Task<CloudQueueMessage> PeekMessageAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.PeekMessageAsync(options, operationContext, cancellationToken);

			//var cloudMessage = await _queue.PeekMessageAsync(options, operationContext, cancellationToken);
			//var message = _serializer.Deserialize(cloudMessage.AsBytes);
			//return message as IMessage;
		}

		public Task<IEnumerable<CloudQueueMessage>> PeekMessagesAsync(int messageCount, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			if (messageCount < 1) throw new ArgumentOutOfRangeException(nameof(messageCount), "must be greather than zero");
			if (messageCount > CloudQueueMessage.MaxNumberOfMessagesToPeek) throw new ArgumentOutOfRangeException(nameof(messageCount), "must be less than or equal to {CloudQueueMessage.MaxNumberOfMessagesToPeek}");

			return _queue.PeekMessagesAsync(messageCount, options, operationContext, cancellationToken);

			//var cloudMessages = await _queue.PeekMessagesAsync(messageCount, options, operationContext, cancellationToken);
			//var messages = cloudMessages.Select(cloudMessage => _serializer.Deserialize(cloudMessage.AsBytes) as IMessage);
			//return messages;
		}

		public Task SetMetadataAsync(QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.SetMetadataAsync(options, operationContext, cancellationToken);
		}

		public Task SetPermissionsAsync(QueuePermissions permissions, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.SetPermissionsAsync(permissions, options, operationContext, cancellationToken);
		}

		public Task UpdateMessageAsync(CloudQueueMessage message, TimeSpan visibilityTimeout, MessageUpdateFields updateFields, QueueRequestOptions options = null, OperationContext operationContext = null, CancellationToken cancellationToken = default(CancellationToken))
		{
			return _queue.UpdateMessageAsync(message, visibilityTimeout, updateFields, options, operationContext, cancellationToken);
		}

		#endregion

		#region PRIVATE METHODS

		private object Deserialize(byte[] serializedContent)
		{
			var serializer = new Serializer();
			using (var stream = new MemoryStream(serializedContent))
			{
				return serializer.Deserialize<object>(stream);
			}

		}

		private byte[] Serialize<T>(T message)
		{
			var serializer = new Serializer();
			using (var stream = new MemoryStream())
			{
				serializer.Serialize(message, stream);
				return stream.ToArray();
			}
		}

		#endregion
	}
}