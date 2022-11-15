#!/bin/bash
## pass in  path as the first process argument
FILE=$1

if [ -n "$FILE" ]; then
    ## set default values
    picklescan=""
    pickescanExitCode=2
    clamscan=""
    clamscanExitCode=2
    fileExists=0

    ## get file
    # echo "Scanning $FILE"
    fileExists=$(ls -l $FILE | wc -l)

    ## if file doesn't exist, exit
    if [ $fileExists -eq 0 ]; then
        # echo "File not found"
        jo -p fileExists="$fileExists" picklescanExitCode="$pickescanExitCode" picklescanOutput="$pickescan" clamscanExitCode="$clamscanExitCode" clamscanOutput="$clamscan"
        exit 1
    fi

    ## run scans
    # echo "Running PickleScan"
    pickescan=$(picklescan -p $FILE -l DEBUG)
    pickescanExitCode=$?

    # echo "Running ClamScan"
    clamscan=$(clamscan $FILE)
    clamscanExitCode=$?

    jo -p fileExists="$fileExists" picklescanExitCode="$pickescanExitCode" picklescanOutput="$pickescan" clamscanExitCode="$clamscanExitCode" clamscanOutput="$clamscan"
else
    echo "Missing file path argument"
fi