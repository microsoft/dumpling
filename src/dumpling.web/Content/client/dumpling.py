#!/usr/bin/env python

# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
# See the LICENSE file in the project root for more information.


import argparse
import os
import zipfile
import string
import platform
import getpass 
import urllib    
import urllib2
import time
import json
import requests
import tempfile
import hashlib  
import zlib
import gzip

class Output:
    s_squelch=False
    s_verbose=False
    s_logPath='';

    @staticmethod
    def Diagnostic(output):
        if not bool(Output.s_squelch) and bool(Output.s_verbose):
            Output.Print(output)
                 
    @staticmethod
    def Message(output):
        if not bool(Output.s_squelch):
            OuputController.Print(output)

    @staticmethod
    def Critical(output):
        OuputController.Print(output)

    @staticmethod
    def Print(output):
        # always print out essential information.
        print output
        
        # sometimes amend our essential output to an existing log file.
        if(Output.s_logPath is not None and os.path.isfile(Output.s_logPath)):
            # Note: The file must exist.
            with open(Output.s_logPath, 'a') as log_file:
                log_file.write(output)

class DumplingService:
    def __init__(self, baseurl):
        self._dumplingUri = baseurl;

    def UploadArtifact(self, dumpid, localpath, hash, file):
        qargs = { }
        
        qargs['hash'] = hash
        qargs['localpath'] = localpath
        
        
        #only include the dumpid if the content is not None
        if dumpid is not None:
            qargs['dumpid'] = dumpid

        url = self._dumplingUri + 'api/artifacts/uploads?' + urllib.urlencode(qargs)

        Output.Diagnostic('uploading artifact %s %s'%(localpath, hash))

        Output.Diagnostic('   url: %s'%(url))

        response = requests.post(url, data=file)
             
        Output.Diagnostic('   response: %s'%(response.content))

        response.raise_for_status()

    def DownloadArtifact(self, strhash, file):              
        url = self._dumplingUri + 'api/artifacts/downloads/' + strhash
        
        response = requests.get(url, stream=True)

        for chunk in response.iter_content(8 * 1024):
            file.write(chunk)
        

class UniqueFileList:
    def __init__(self):
        self._hashmap = { }

    def Add(self, abspath):
        if not self._hashmap.has_key(abspath):
            self._hashmap[abspath] = None
            return True
        else:
            return False

    def AddPaths(self, incpaths):
        for p in incpaths:
            abspath = os.path.abspath(p)
            if os.path.isdir(abspath):
                for dirpath, dirnames, filenames in os.walk(abspath):
                    for name in filenames:
                        subpath = os.path.join(dirpath, name)
                        if self.Add(subpath):
                            yield subpath
            else:
                if self.Add(abspath):
                    yield abspath

def HashAndCompress(fDecomp, fComp):
    BLOCKSIZE = 1024 * 1024
    compsize = 0
    hash = hashlib.sha1()
    buf = fDecomp.read(BLOCKSIZE)
    while len(buf) > 0:
        hash.update(buf)
        fComp.write(buf)       
        buf = fDecomp.read(BLOCKSIZE)
    fComp.flush() 
    return hash.hexdigest() 

def QueueFileDownload(dumpSvc, hash, abspath):
    
def QueueFileUpload(dumpSvc, dumpid, abspath):
    hash = None
    tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp());
    with gzip.open(tempPath, 'wb') as fComp:
        with open(abspath, 'rb') as fDecomp:
            hash = HashAndCompress(fDecomp, fComp)
    with open(tempPath, 'rb') as fUpld:
        dumpSvc.UploadArtifact(dumpid, abspath, hash, fUpld)    
    os.remove(tempPath)
    
#def UploadDump(dumppath, incpaths):
#    #

def UploadFiles(dumpSvc, dumpid, incpaths):
    #
    flist = UniqueFileList()
    for abspath in flist.AddPaths(incpaths):
        QueueFileUpload(dumpSvc, dumpid, abspath)

def Upload(uploadArgs, dumpSvc):
    #if nothing was specified to upload
    if args.dumppath is None and (args.incpaths is None or len(args.incpaths) == 0):
        Output.Critical('No artifacts or dumps were specified to upload, either --dumppath or --incpaths is required to upload')

    #if dumppath and dumpid are specified      
    if args.dumppath is not None and args.dumpid is not None:
        Output.Critical('Argument --dumppath is not supported when specifying --dumpid.  Use --incpaths to associate more files with an existing dump')

    #if dumppath was specified call upload dump
    if args.dumppath is not None:
        UploadDump(args.dumppath, args.incpaths)
    #otherwise call upload files
    else:
        UploadFiles(dumpSvc, args.dumpid, args.incpaths)
        
 def Download(downloadArgs, dumpSvc):
          

if __name__ == '__main__':

    outputparser = argparse.ArgumentParser(add_help=False)
    
    outputparser.add_argument('--verbose', default=False, action='store_true', help='Indicates that we should print all critical, standard, and diagnostic messages.')

    outputparser.add_argument('--squelch', default=False, action='store_true', help='Indicates that we should only print critical messages.')
                                 
    outputparser.add_argument('--logpath', type=str, help='specify the path to a log file for appending message output.')

    parser = argparse.ArgumentParser(parents=[outputparser], description='dumpling client for managing core files and interacting with the dumpling service')
    
    subparsers = parser.add_subparsers(title='command', dest='command')
    
    upload_parser = subparsers.add_parser('upload', parents=[outputparser], help='command used for uploading dumps and files to the dumpling service')

    upload_parser.add_argument('--dumppath', type=str, help='path to the dumpfile to be uploaded')
                                                                                                     
    upload_parser.add_argument('--dumpid', type=int, help='the dumpling id the specified files are to be associated with')

    upload_parser.add_argument('--incpaths', nargs='*', type=str, help='paths to files or directories to be included upload')
   
    download_parser = subparsers.add_parser('download', parents=[outputparser], help='command used for downloading dumps and files from the dumpling service')    
                                                                                                     
    download_parser.add_argument('--dumpid', type=int, help='the dumpling id of the dump to download for debugging')   
    
    download_parser.add_argument('--hash', type=int, help='the id of the artifact to download')
        
    download_parser.add_argument('--outdir', type=int, help='the directory to download the spefied files to')



    args = parser.parse_args()
    
    Output.s_verbose = args.verbose
    Output.s_squelch = args.squelch
    Output.s_logPath = args.logpath


    if args.command == 'upload':
        Upload(args, DumplingService('http://localhost:2399/'))


