## ImageResizer Azure Blob Storage Cache Plugin

An Azure blob storage cache plugin for Image Resizer .NET

## Usage
Add this settings to web.config file:
```
<resizer>
   <plugins>
      <add name="AzureBlobStorageCache" />
   </plugins>
   <azureblobstoragecache connectionStringName="your_blob_connection_string_name" cacheAccessTimeout="timeout_in_seconds" container="your_blob_container" />
</resizer>
```

## Build the package
To build the package, just compile the project and run the buildpackage.ps1 powershell script.
