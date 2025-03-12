# chataigne-speech-to-text

Chataigne-speech-to-text is a Chataigne module that integrates [speech to text google cloud API (v2)](https://cloud.google.com/speech-to-text).


## Introduction

Chataigne-OpenPose allows users to capture and analyze human motion using Open Pose and integrate them into Chataigne. This integration enables real-time motion tracking for various applications such as games, art performances and more.


## Prerequisites

The installation process assumes that you have Dotnet installed.

You also need to create a google cloud project, enable Speech-to-text API and set up a service-account in order to obtain a service key.

For further details check:
[Dotnet installation](https://dotnet.microsoft.com/fr-fr/download)
[Google cloud speech API v2](https://cloud.google.com/speech-to-text)
[Set up a service account](https://console.cloud.google.com/iam-admin/serviceaccounts)

## Installation

To install Chataigne-speech-to-text, follow these steps:

1. [Download the server content](https://github.com/PierreLemmel/chataigne-speech-to-text/releases/download/Server/speech-to-text-server.zip)
2. Extract the *server* folder
3. Add the Speech to text module inside Chataigne
4. In Transcription settings, specify the path to the *speech-server.bat* file, the *service-account.json* file (see prerequisites) and add the key to your Google Cloud project.
5. Enjoy 

## License

This project is licensed under the Apache License 2.0. See the [LICENSE](LICENSE) file for more details.
