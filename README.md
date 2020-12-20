# Azure Search Functions

## What does the function do?
The Azure function in this Repository creates als needed Azure resources that you need to index data from [PokeApi](https://pokeapi.co/). In detail it will go through following steps:
 - Clears the blob storage from previous downloaded data
 - Downloads all data from the PokeApi and stores it in the blob storage
 - Deletes the old index and creates a new one
 - Deletes the old indexer and creates a new one
 - Runs the indexer and fill the data with the downloaded PokeApi data.

You can find detailed information on [LINK FROM ARTICLE HERE](https://thinktecture.com).

## Configuration

To use the Azure function you have to set following environment  variables:
- SEARCH_SERVICE_NAME: Name of your Azure Search Service.
- SEARCH_API_KEY: Api key of the search service.
- AzureBlogStorageConnectionString: Connection String of your blob storage.
- IndexContainerName: Azure blob storage container name to store the PokeApi data;

## Testing
To test this function you can either do a POST request to the Azure published function or to your local started build. The url looks like:

http://localhost:7071/api/rebuild-index/{count}

As {count} you use the number of Pokemon you want to index. The number has to be between 1 and 899. When the function run was successful you will get following response message:

`Index successfully re-created`

If the index creation was finished you will be able to use your Azure search index and query the indexed PokeApi data.

## Frontend

To use the `index.html` in the fronted folder you have to update following keys in the file:
- YOUR_INDEX_NAME: Your Azure Search index name
- YOUR_SEARCH_KEY: Your Azure Search key
- YOUR_SEARCH_SERVICE: Name of your Azure Search Service

After you have update the `index.html` you can just open it in a browser and search your indexed data.