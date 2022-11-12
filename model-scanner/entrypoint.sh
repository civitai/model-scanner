#!/bin/bash
## pass in URL as an environment variable
URL=$1

if [ -n "$URL" ]; then
    ## set default values
    picklescan=""
    pickescanExitCode=2
    clamscan=""
    clamscanExitCode=2
    fileExists=0

    ## get file
    echo "Scanning $URL"
    curl -s -f -L -o "model.bin" "$URL"
    fileExists=$(ls -l model.bin | wc -l)

    ## if file doesn't exist, exit
    if [ $fileExists -eq 0 ]; then
        echo "File not found"
        jo -p url="$URL" fileExists="$fileExists" picklescanExitCode="$pickescanExitCode" picklescanOutput="$pickescan" clamscanExitCode="$clamscanExitCode" clamscanOutput="$clamscan"
        exit 1
    fi

    ## run scans
    echo "Running PickleScan"
    pickescan=$(picklescan -p model.bin -l DEBUG)
    pickescanExitCode=$?

    echo "Running ClamScan"
    clamscan=$(clamscan model.bin)
    clamscanExitCode=$?

    jo -p url="$URL" fileExists="$fileExists" picklescanExitCode="$pickescanExitCode" picklescanOutput="$pickescan" clamscanExitCode="$clamscanExitCode" clamscanOutput="$clamscan"
else
    echo "Missing url environment variable"
fi