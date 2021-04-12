## BlobCopyListJob

This is the service that receives a service bus message and begins scanning the source. The results
are written to a redis list. Ensure you have enough workers to stop the list from becoming to large (running out of redis mem).

This doesn't end gracefully. Has a message lock error or something. Ignore it.

## BlobCopyWorkerJob

This service sends the http request to copy the blob from container to container. It reads entries from the work list in batches of 500.
Run many of these (I used 4 to 5).

# Also...

This depends on Redis. Its hardcoded to use SSL to access it also. It also depends on service bus as an entry point for the copy job. An ah... azure storage in case that wasn't clear.