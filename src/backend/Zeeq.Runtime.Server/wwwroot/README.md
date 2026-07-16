# Public Content Root

This is the build target root for the two Vue-based frontends (app and docs) which are served from the Zeeq.Runtime.Server's wwwroot. The contents of this folder are copied from the web and docs build outputs during the Docker build process. This folder is not used during development, as the server is configured to proxy requests to the Vue dev servers for both app and docs.
