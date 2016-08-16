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
    s_inst=None

    def __init__(self, baseurl):
        self._dumplingUri = baseurl;

    def UploadArtifact(self, dumpid, localpath, hash, file):
        
        qargs = { 'hash': hash, 'localpath': localpath }
        
        #only include the dumpid if the content is not None
        if dumpid is not None:
            qargs['dumpid'] = dumpid

        url = self._dumplingUri + 'api/artifacts/uploads?' + urllib.urlencode(qargs)

        Output.Message('uploading artifact %s %s'%(localpath, hash))

        Output.Diagnostic('   url: %s'%(url))

        response = requests.post(url, data=file)
             
        Output.Diagnostic('   response: %s'%(response.content))

        response.raise_for_status()

    
    def DownloadArtifact(self, strhash, file):              
        url = self._dumplingUri + 'api/artifacts/downloads/' + strhash
        
        response = requests.get(url, stream=True)

        Output.Diagnostic(response.headers)

        Output.Diagnostic('')

        Output.Diagnostic(response)

    def CreateDump(self, origin, displayname):
        qargs = { 'origin': origin, 'displayname': displayname }
        
        url = self._dumplingUri + 'api/dumplings/create?' + urllib.urlencode(qargs)

        response = requests.get(url)

        dumpData = repsone.json() 
        
        Output.Message('Created dump: %s'%dumpData.dumpId)

        Output.Diagnostic('raw dump data: %s'%dumpData)

        return dumpData.DumpId

            

    def UploadDump(self, dumpid, localpath, hash, file):    
        qargs = { 'hash': hash, 'localpath': localpath, 'dumpid': dumpid,  }

        url = self._dumplingUri + 'api/dumplings/' + dumpid + '/dumps/uploads?' + urllib.urlencode(qargs)

        Output.Message('uploading artifact %s %s'%(localpath, hash))

        Output.Diagnostic('   url: %s'%(url))

        response = requests.post(url, data=file)
             
        Output.Diagnostic('   response: %s'%(response.content))

        response.raise_for_status()        


        
class FileTransferManager:
    def __init__(self, dumpSvc):
        self._hashmap = { }
        self._dumpSvc = dumpSvc

    def QueueFileDownload(self, hash, abspath):
        #with open(abspath, 'wb+') as file:
        self._dumpSvc.DownloadArtifact(hash, None)

    def QueueFileUpload(self, dumpid, abspath):
        hash = None
        tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp())
        with gzip.open(tempPath, 'wb') as fComp:
            with open(abspath, 'rb') as fDecomp:
                hash = _hash_and_compress(fDecomp, fComp)
        with open(tempPath, 'rb') as fUpld:
            self._dumpSvc.UploadArtifact(dumpid, abspath, hash, fUpld)    
        os.remove(tempPath)
    
    def UploadDump(self, dumpid, dumppath, incpaths):
        #
        hash = None
        tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp())
        with gzip.open(tempPath, 'wb') as fComp:
            with open(abspath, 'rb') as fDecomp:
                hash = _hash_and_compress(fDecomp, fComp)
        with open(tempPath, 'rb') as fUpld:
            dumpArtifacts = self._dumpSvc.UploadDump(dumpid, abspath, hash, fUpld)  
        refpaths = [da.path for da in dumpArtfacts.iteritems()]
        self.UploadFiles(dumpid, refpaths)

    def UploadFiles(self, dumpid, incpaths):
        #
        for abspath in self._add_upload_paths(incpaths):
            self.QueueFileUpload(dumpid, abspath)          

    def _add_upload_path(self, abspath):
        if not self._hashmap.has_key(abspath):
            self._hashmap[abspath] = None
            return True
        else:
            return False

    def _add_upload_paths(self, incpaths):
        for p in incpaths:
            abspath = os.path.abspath(p)
            if os.path.isdir(abspath):
                for dirpath, dirnames, filenames in os.walk(abspath):
                    for name in filenames:
                        subpath = os.path.join(dirpath, name)
                        if self._add_upload_path(subpath):
                            yield subpath
            else:
                if self._add_upload_path(abspath):
                    yield abspath

    @staticmethod
    def _hash_and_compress(fDecomp, fComp):
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


class CommandProcesser:
    def __init__(self, args, filequeue, dumpSvc):
        self._args = { }
        self._dumpSvc = dumpSvc
        self._filequeue = filequeue

    def Upload(self):
        #if nothing was specified to upload
        if self._args.dumppath is None and (self._args.incpaths is None or len(self._args.incpaths) == 0):
            Output.Critical('No artifacts or dumps were specified to upload, either --dumppath or --incpaths is required to upload')

        #if dumppath and dumpid are specified      
        if self._args.dumppath is not None and self._args.dumpid is not None:
            Output.Critical('Argument --dumppath is not supported when specifying --dumpid.  Use --incpaths to associate more files with an existing dump')

        #if dumppath was specified call create dump and upload dump
        if args.dumppath is not None:

            dumpArtifacts = UploadDump(args.dumpid, args.dumppath, args.incpaths)

            incpaths = [da.LocalPath for da in dumpArtifacts.iteritems()]

            UploadFiles
                
                
                    
        #if there are included paths upload them
        if not (self._args.incpaths is None or len(self._args.incpaths) == 0):
            UploadFiles(args.dumpid, args.incpaths)
        
    def Download(downloadArgs):
        #TODO: ERROR Handling
        QueueFileDownload(downloadArgs.hash, None)

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
                                                
    upload_parser.add_argument('--displayname', type=str, default='%s.%.7f'%(getpass.getuser().lower(), time.time), help='the name to be displayed in reports for the uploaded dump.  This argument is ignored unless --dumppath is specified')

    upload_parser.add_argument('--user', type=str, default=getpass.getuser().lower(), help='The username to pass to the dumpling service.  This argument is ignored unless --dumppath is specified')

    upload_parser.add_argument('--incpaths', nargs='*', type=str, help='paths to files or directories to be included upload')

   
    download_parser = subparsers.add_parser('download', parents=[outputparser], help='command used for downloading dumps and files from the dumpling service')    
                                                                                                     
    download_parser.add_argument('--dumpid', type=int, help='the dumpling id of the dump to download for debugging')   
    
    download_parser.add_argument('--hash', type=str, help='the id of the artifact to download')
        
    download_parser.add_argument('--outdir', type=str, help='the path to the directory to download the specified content')



    args = parser.parse_args()
    
    Output.s_verbose = args.verbose
    Output.s_squelch = args.squelch
    Output.s_logPath = args.logpath

    DumplingService.s_inst = DumplingService('http://localhost:2399/')

    if args.command == 'upload':
        Upload(args)
    elif args.command == 'download':
        Download(args)


