﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.File;
using Microsoft.WindowsAzure.Storage.Queue;
using Microsoft.WindowsAzure.Storage.Table;
using Picton.Interfaces;
using System;

namespace Picton
{
	public class StorageAccount : IStorageAccount
	{
		#region FIELDS

		private readonly CloudStorageAccount _cloudStorageAccount;

		#endregion

		#region CONSTRUCTOR

		public StorageAccount(CloudStorageAccount cloudStorageAccount)
		{
			_cloudStorageAccount = cloudStorageAccount;
		}

		public StorageAccount(StorageCredentials storageCredentials, bool useHttps)
		{
			_cloudStorageAccount = new CloudStorageAccount(storageCredentials, useHttps);
		}

		public StorageAccount(StorageCredentials storageCredentials, string endpointSuffix, bool useHttps)
		{
			_cloudStorageAccount = new CloudStorageAccount(storageCredentials, endpointSuffix, useHttps);
		}

		public StorageAccount(StorageCredentials storageCredentials, string accountName, string endpointSuffix, bool useHttps)
		{
			_cloudStorageAccount = new CloudStorageAccount(storageCredentials, accountName, endpointSuffix, useHttps);
		}

		public StorageAccount(StorageCredentials storageCredentials, Uri blobEndpoint, Uri queueEndpoint, Uri tableEndpoint, Uri fileEndpoint)
		{
			_cloudStorageAccount = new CloudStorageAccount(storageCredentials, blobEndpoint, queueEndpoint, tableEndpoint, fileEndpoint);
		}

		public StorageAccount(StorageCredentials storageCredentials, StorageUri blobStorageUri, StorageUri queueStorageUri, StorageUri tableStorageUri, StorageUri fileStorageUri)
		{
			_cloudStorageAccount = new CloudStorageAccount(storageCredentials, blobStorageUri, queueStorageUri, tableStorageUri, fileStorageUri);
		}

		#endregion

		#region STATIC METHODS

		public static StorageAccount FromCloudStorageAccount(CloudStorageAccount cloudStorageAccount)
		{
			if (cloudStorageAccount == null) return null;
			return new StorageAccount(cloudStorageAccount);
		}

		#endregion

		#region PUBLIC METHODS

		public CloudBlobClient CreateCloudBlobClient()
		{
			return _cloudStorageAccount.CreateCloudBlobClient();
		}

		public CloudFileClient CreateCloudFileClient()
		{
			return _cloudStorageAccount.CreateCloudFileClient();
		}

		public CloudQueueClient CreateCloudQueueClient()
		{
			return _cloudStorageAccount.CreateCloudQueueClient();
		}

		public CloudTableClient CreateCloudTableClient()
		{
			return _cloudStorageAccount.CreateCloudTableClient();
		}

		public string GetSharedAccessSignature(SharedAccessAccountPolicy policy)
		{
			return _cloudStorageAccount.GetSharedAccessSignature(policy);
		}

		public override string ToString()
		{
			return ToString(false);
		}

		public string ToString(bool exportSecrets)
		{
			return _cloudStorageAccount.ToString(exportSecrets);
		}

		#endregion
	}
}
