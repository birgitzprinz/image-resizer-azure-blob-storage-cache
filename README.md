# image-resizer-epi-blob-storage-cache
An Azure blob storage cache plugin for Image Resizer .NET
Usage:

<resizer>
   <plugins>
      <add name="AzureBlobStorageCache" />
   </plugins>
   <azureblobstoragecache connectionStringName="BlobConnection" cacheAccessTimeout="30" container="scaledimages1" />
</resizer>