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
import threading
import multiprocessing
import datetime
import copy
import stat

def _json_format(obj):
    return json.dumps(obj, sort_keys=True, indent=4, separators=(',', ': '))

def _json_format_tofile(obj, file):
    json.dump(obj, sort_keys=True, indent=4, separators=(',', ': '))

class Output:
    s_squelch=False
    s_verbose=False
    s_noprompt=False
    s_logPath=''
    s_lock=threading.Lock()

    @staticmethod
    def Diagnostic(output):
        if not bool(Output.s_squelch) and bool(Output.s_verbose):
            Output.Print(output)
                 
    @staticmethod
    def Message(output):
        if not bool(Output.s_squelch):
            Output.Print(output)

    @staticmethod
    def Critical(output):
        Output.Print(output)

    @staticmethod
    def Print(output):
        Output.s_lock.acquire()

        try:
            # always print out essential information.
            print output
        
            # sometimes amend our essential output to an existing log file.
            if(Output.s_logPath is not None and os.path.isfile(Output.s_logPath)):
                # Note: The file must exist.
                with open(Output.s_logPath, 'a') as log_file:
                    log_file.write(output)
        finally:
            Output.s_lock.release()
     
    @staticmethod
    def Prompt_YN(prompt):
        if Output.s_noprompt or Output.s_squelch:
            return True
        Output.s_lock.acquire()
        result = None
        try:
            while(result != 'y' and result != 'n'):
                result = raw_input(prompt + ' [Y/N]: ').lower()
        finally:
            Output.s_lock.release()
        return result == 'y'

class FileUtils:

    @staticmethod
    def _hash_and_compress(inpath, outpath):     
        FileUtils._ensure_dir(outpath)
        with gzip.open(outpath, 'wb') as fComp:
            with open(inpath, 'rb') as fDecomp:
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

    @staticmethod
    def _hash_and_decompress(inpath, outpath):
        FileUtils._ensure_dir(outpath)
        with gzip.open(inpath, 'rb') as fComp:
            with open(outpath, 'wb') as fDecomp:
                BLOCKSIZE = 1024 * 1024
                compsize = 0
                hash = hashlib.sha1()
                buf = fComp.read(BLOCKSIZE)
                while len(buf) > 0:
                    hash.update(buf)
                    fDecomp.write(buf)       
                    buf = fComp.read(BLOCKSIZE)
                fDecomp.flush() 
                return hash.hexdigest() 

    @staticmethod    
    def _ensure_dir(path):
        dir = os.path.dirname(path)    
        #create the directory if it doesn't exist
        if not os.path.isdir(os.path.abspath(dir)):
            try:
                os.makedirs(os.path.abspath(dir))
            except:
                return

class DumplingService:
    s_inst=None

    def __init__(self, baseurl):
        self._dumplingUri = baseurl;

    def DownloadDebugger(self, outputdir):
        url = self._dumplingUri + 'api/client/tools/debug?'
                               
        osStr = platform.system().lower()

        qargs = { 'os': osStr }

        if osStr == 'linux':
            qargs['distro'] = platform.dist()[0].lower()

        url = url + urllib.urlencode(qargs)

        Output.Message('downloading debugger for client %s'%('-'.join(qargs.values())))
                                                               
        Output.Diagnostic('   url: %s'%(url))
               
        response = requests.get(url);
                  
        response.raise_for_status()
        
        Output.Diagnostic('   response: %s'%(response))

        Output.Diagnostic('   headers: %s'%(response.headers))

        FileUtils._ensure_dir(outputdir)

        DumplingService._stream_zip_archive_from_response(response, outputdir)
        
        dbgPath = 'cdb.exe' if osStr == 'windows' else 'bin/lldb'

        dbgPath = os.path.join(outputdir, dbgPath)
        
        Output.Diagnostic('   dbgpath: %s'%(dbgPath))

        return  dbgPath                                                         

    def GetDumplingManfiest(self, dumpid):
        url = self._dumplingUri  + 'api/dumplings/' + dumpid + '/manifest'

        Output.Message('retrieving dumpling %s manifest'%(dumpid))    

        Output.Diagnostic('   url: %s'%(url))

        response = requests.get(url);
                          
        Output.Diagnostic('   response: %s'%(response))
                                                          
        Output.Diagnostic('   content: %s'%(_json_format(response.json())))
        
        return response.json()

    def UploadArtifact(self, dumpid, localpath, hash, file):
        
        qargs = { 'hash': hash, 'localpath': localpath }
        
        url = self._dumplingUri  + 'api/'

        #only include the dumpid if the not None
        if dumpid is not None:
            url = url + 'dumplings/' + dumpid + '/'

        url = url + 'artifacts/uploads?' + urllib.urlencode(qargs)

        Output.Message('uploading artifact %s %s'%(hash, os.path.basename(localpath)))

        Output.Diagnostic('   url: %s'%(url))

        response = requests.post(url, data=file)

        Output.Diagnostic('   response: %s'%(response.content))

        response.raise_for_status()

    
    def DownloadArtifact(self, hash, downpath):  
        if os.path.isdir(downpath):
            self._dumpSvc.DowloadArtifactToDirectory(hash, downpath)

            return

        url = self._dumplingUri + 'api/artifacts/' + hash

        Output.Diagnostic('   url: %s'%(url))
        
        response = requests.get(url, stream=True)
                                                     
        Output.Diagnostic('   response: %s'%(response))
                                    
        response.raise_for_status()

        DumplingService._stream_compressed_file_from_response(response, hash, downpath)
        
    def DowloadArtifactToDirectory(self, hash, dirpath):
        url = self._dumplingUri + 'api/artifacts/' + hash

        Output.Diagnostic('   url: %s'%(url))
        
        response = requests.get(url, stream=True)
                                                     
        Output.Diagnostic('   response: %s'%(response))
                                    
        response.raise_for_status()

        #find the first dumpling-filename in the history of response headers, we need to look through the history b/c of the redirects involved
        filename = next((hist for hist in response.history if next((val for h, val in hist.headers if h == 'dumpling-filename'), None) is not None), tempfile.mktemp())
        
        downpath = os.path.join(dirpath, filename)
        
        DumplingService._stream_compressed_file_from_response(response, hash, downpath)
        
    def UploadDump(self, localpath, hash, origin, displayname, file):    
        qargs = { 'hash': hash, 'localpath': localpath, 'origin': origin, 'displayname': displayname  }

        url = self._dumplingUri + 'api/dumplings/uploads?' + str(urllib.urlencode(qargs))

        Output.Message('uploading artifact %s %s'%(hash, os.path.basename(localpath)))

        Output.Diagnostic('   url: %s'%(url))

        response = requests.post(url, data=file)
                                     
        Output.Diagnostic('   response: %s'%(response))
                    
        response.raise_for_status()

        return response.json()
    
    def UpdateDumpProperties(self, dumplingid, dictProps):
        url = self._dumplingUri + 'api/dumplings/' + dumplingid + '/properties'
        
        Output.Diagnostic('   url: %s'%(url))

        Output.Diagnostic('   data: %s'%(_json_format(dictProps)))

        response = requests.post(url, data=dictProps)    

        response.raise_for_status()
                          
        Output.Diagnostic('   response: %s'%(response))

    @staticmethod
    def _stream_zip_archive_from_response(response, unpackdir):
        #write the zip archive a temp file
        tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp())
        with open(tempPath, 'wb') as fd:
            for chunk in response.iter_content(1024*8):
                fd.write(chunk)
        
        with open(tempPath, 'rb') as tempFile:
            zip = zipfile.ZipFile(tempFile)
            for path in zip.namelist():
                Output.Diagnostic('extracting   ' + path)
                zip.extract(path, unpackdir)
            zip.close()
        
        os.remove(tempPath)
        
    @staticmethod
    def _stream_compressed_file_from_response(response, hash, path):
        #write the compressed blob to a temp file
        tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp())
        with open(tempPath, 'wb') as fd:
            for chunk in response.iter_content(1024*8):
                fd.write(chunk)
        
        downhash = FileUtils._hash_and_decompress(tempPath, path)
        
        os.remove(tempPath)

        if downhash != hash:
            Output.Critical("ERROR: downloaded file did not match expected hash value")
            os.remove(path)
        else: 
            Output.Message('downloaded artifact %s %s'%(hash, os.path.basename(path)))                

class ThreadPool:
    s_MaxThreads = multiprocessing.cpu_count()

    def __init__(self, maxthreads = None):
        self._condvar = threading.Condition(threading.RLock())
        self._threadcount = 0
        self._availcount = 0
        self._queue = [ ]
        self._emptyevent = threading.Event()
        self._maxthreads = maxthreads or ThreadPool.s_MaxThreads
        self._draining = False
    
    def queue_work(self, func, args=()):
        self._condvar.acquire()

        if not self._draining:
            self._queue.append( (func, args) )

            if self._availcount == 0 and self._threadcount < self._maxthreads:
                self._threadcount += 1
                self._add_thread()

            self._condvar.notify()
        else:
            raise RuntimeError('the thread pool is currently draining and not available to queue work items')

        self._condvar.release()
            
    def drain(self):
        self._condvar.acquire()
        self._draining = True
        self._condvar.release()
        self._emptyevent.wait()

    def _add_thread(self):
        thread = threading.Thread(target=self._process_queue_items, args=())
        thread.start()

    def _process_queue_items(self):
        while True:
            self._condvar.acquire()
            if len(self._queue) == 0: 
                if self._draining:
                    self._condvar.notify_all()
                    self._thread_exit()
                    self._condvar.release()             
                    return
                self._availcount = self._availcount + 1
                self._condvar.wait()
                self._availcount = self._availcount - 1
            work = self._queue.pop(0)
            func = work[0]
            args = work[1]
            self._condvar.release()
            try:
                func(*args[0:])
            except:
                self._thread_exit()
                raise

    def _thread_exit(self):
        self._condvar.acquire()                     
        self._threadcount -= 1
        if self._draining and len(self._queue) == 0 and self._threadcount == 0:
            self._emptyevent.set() 
        self._condvar.release()   
        
class FileTransferManager:

    def __init__(self, dumpSvc, maxthreads = None):
        self._hashmap = { }
        self._dumpSvc = dumpSvc
        self._threadpool = ThreadPool(maxthreads)
        self._threadpool._maxthreads
         
    def QueueFileDownload(self, hash, abspath):
        if self._threadpool._maxthreads <= 1:
            self._dumpSvc.DownloadArtifact(hash, abspath)
        else:                                       
            self._threadpool.queue_work(self._dumpSvc.DownloadArtifact, args=(hash, abspath))
        
    def QueueFileUpload(self, dumpid, abspath):
        if self._threadpool._maxthreads <= 1:
            self._compress_and_upload(dumpid, abspath)
        else:                                                                                     
            self._threadpool.queue_work(self._compress_and_upload, args=(dumpid, abspath))

    def WaitForPendingTransfers(self):
        if self._threadpool._maxthreads > 1:
            self._threadpool.drain()

    def _compress_and_upload(self, dumpid, abspath):              
        hash = None
        Output.Diagnostic('uncompressed file size: %s Kb'%(str(os.path.getsize(abspath) / 1024)))
        tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp())
        try:
            fhash = FileUtils._hash_and_compress(abspath, tempPath)
            Output.Diagnostic('compressed file size:   %s Kb'%(str(os.path.getsize(tempPath) / 1024)))
            with open(tempPath, 'rb') as fUpld:
                self._dumpSvc.UploadArtifact(dumpid, abspath, fhash, fUpld)    
        
        finally:
            try:
                os.remove(tempPath)
            except:
                Output.Message('WARNING: failed to remove temp file %s'%(tempPath))
                

    def UploadDump(self, dumppath, incpaths, origin, displayname):
        #
        hash = None
        Output.Diagnostic('uncompressed file size: %s Kb'%(str(os.path.getsize(dumppath) / 1024)))
        tempPath = os.path.join(tempfile.gettempdir(), tempfile.mktemp())
        hash = FileUtils._hash_and_compress(dumppath, tempPath)
        Output.Diagnostic('compressed file size:   %s Kb'%(str(os.path.getsize(tempPath) / 1024)))
        with open(tempPath, 'rb') as fUpld:
            dumpData = self._dumpSvc.UploadDump(dumppath, hash, origin, displayname, fUpld)   
            self.UploadFiles(dumpData['dumplingId'], dumpData['refPaths'])
        os.remove(tempPath)
        return dumpData['dumplingId']

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
            p = p.rstrip('\\')
            abspath = os.path.abspath(p)
            if os.path.isdir(abspath):
                for dirpath, dirnames, filenames in os.walk(abspath):
                    for name in filenames:
                        subpath = os.path.join(dirpath, name)
                        if self._add_upload_path(subpath):
                            yield subpath
            elif os.path.isfile(abspath):
                if self._add_upload_path(abspath):
                    yield abspath

class CommandProcesser:
    def __init__(self, args, filequeue, dumpSvc):
        self._args = args
        self._dumpSvc = dumpSvc
        self._filequeue = filequeue

    def Process(self):
        if self._args.command == 'upload':
            self.Upload()
        elif self._args.command == 'download':
            self.Download()
        elif self._args.command == 'config':
            self.Config()
        elif self._args.command == 'install':
            self.Install()
        elif self._args.command == 'debug':
            self.Debug()
     
    def Install(self):
        if self._args.debug:    
            dbgPath = self._dumpSvc.DownloadDebugger(os.path.join(self._args.installpath, 'dbg'))
            if platform.system().lower() != 'windows':
                 os.chmod(dbgPath, stat.S_IEXEC)
            Output.Message('Adding debugger settings dumpling config')
            DumplingConfig.SaveSettings(self._args.configpath, { 'dbgpath': dbgPath })
            Output.Message('Debugger successfully installed')
            
                
            
    def Config(self):
        if self._args.action == 'dump':
            config = DumplingConfig.Load(self._args.configpath)
            if config is None:
                Output.Message('no dumpling configuration file was found')            
            else:
                Output.Message(str(config))
        
        if self._args.action == 'save':
            Output.Message(str(self._args))
            self._args.Save(self._args.configpath)
                                                               
        if self._args.action == 'clear':
            if os.path.isfile(self._args.configpath) and Output.Prompt_YN('Delete file "%s"?'%(self._args.configpath)):
                os.remove(self._args.configpath)
                Output.Message('Configuration cleared') 
            else:
                Output.Message('Command aborted. No changes were made.')                                                                                                                       
            

    def Upload(self):
        #if nothing was specified to upload
        if self._args.dumppath is None and (self._args.incpaths is None or len(self._args.incpaths) == 0):
            Output.Critical('No artifacts or dumps were specified to upload, either --dumppath or --incpaths is required to upload')

        #if dumppath was specified call create dump and upload dump
        if self._args.dumppath is not None:

            self._args.dumppath = os.path.abspath(self._args.dumppath)

            if self._args.displayname is None:
                self._args.displayname = str('%s.%.7f'%(getpass.getuser().lower(), time.time()))

            dumpid = self._filequeue.UploadDump(self._args.dumppath, self._args.incpaths, self._args.user, self._args.displayname)
            
            if self._args.properties is not None:
                propDict = { }
                for kvp in self._args.properties:
                    if kvp is not None:
                        CommandProcesser._add_key_if_not_exists(dictProp,kvp[0],kvp[1])
                self._args.properties = propDict
            
            if not self._args.suppresstriage:
                self._args.properties = CommandProcesser._add_client_triage_properties(self._args.properties)

            if self._args.properties is not None:
                self._dumpSvc.UpdateDumpProperties(dumpid, self._args.properties)      
            
        #if there are included paths upload them
        if not (self._args.incpaths is None or len(self._args.incpaths) == 0):
            self._filequeue.UploadFiles(dumpid, args.incpaths)

        self._filequeue.WaitForPendingTransfers();

        
    def Download(self):
        
        #choose download path argument downpath takes precedents since downdir has a defualt
        path = self._args.downpath or self._args.downdir

        path = os.path.abspath(path)

        #determine the directory of the intended download 
        dir = os.path.dirname(path) if self._args.downpath else path
            
        #create the directory if it doesn't exist
        if not os.path.isdir(dir):
            FileUtils._ensure_dir(dir)
        
        if self._args.hash is not None:        
            self._filequeue.QueueFileDownload(self._args.hash, abspath)
            self._filequeue.WaitForPendingTransfers();

        elif self._args.symindex is not None:
            Output.Critical('downloading artifacts from index is not yet supported')
            #self._filequeue.QueueFileIndexDownload(self._args.symindex, abspath)

        elif self._args.dumpid is not None:  
            dumpManifest = self._dumpSvc.GetDumplingManfiest(self._args.dumpid)
            
            self._download_dump(dumpManifest)


    def Debug(self):
        if self._args.dbgpath is None:
            Output.Critical('dbgpath must be specified either as an argument or in the dumpling config to use the debug command')
            return
        
        #get the dump manifest                           
        dumpManifest = self._dumpSvc.GetDumplingManfiest(self._args.dumpid)
        
        #if the dump OS is not debuggable on this system error and return
        if dumpManifest['oS'] != platform.system().lower():
            Output.Critical('the specified dump can only be debugged on the %s platform'%(dumpManifest['oS']))
            return

        #donwload the dump
        self._download_dump(dumpManifest)
        
        #find the dump path from the manifest
        

    def _download_dump(self, dumpManifest):
        dumplingDir = os.path.join(dir, dumpManifest['displayName'])
            
        if not os.path.exists(dumplingDir):
            FileUtils._ensure_dir(dumplingDir)

        #download all the artifacts for the dump
        for da in dumpManifest['dumpArtifacts']:
            if 'hash' in da and 'relativePath' in da:
                hash = da['hash']
                relPath = da['relativePath']
                if hash and relPath:
                    self._filequeue.QueueFileDownload(hash, os.path.join(dumplingDir, relPath)) 
        
        #save the manifest at the root 
        manifestPath = os.path.join(dumplingDir, 'dumpling.manifest.json')

        with open(manifestPath, 'w') as manFile:
            _json_format_tofile(dumpManifest, manFile)
 
        return dumpManifest

        
    @staticmethod
    def _add_client_triage_properties(dictProp):
        dictProp = dictProp or { }
        CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_ARCHITECTURE', platform.machine())
        CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_PROCESSOR', platform.processor())
        CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_NAME', platform.node())
        CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_OS', platform.system())           
        CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_RELEASE', platform.release())     
        CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_VERSION', platform.version())
        if platform.system() == 'Linux':
            distroTuple = platform.linux_distribution()
            CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_DISTRO', distroTuple[0])
            CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_DISTRO_VER', distroTuple[1])
            CommandProcesser._add_key_if_not_exists(dictProp, 'CLIENT_DISTRO_ID', distroTuple[2])
        return dictProp

    @staticmethod
    def _add_key_if_not_exists(dictProp, key, val):
        if not key in dictProp:
            dictProp[key] = val

class DumplingConfig:

    s_unsaved_args = { 'action', 'command', 'configpath', 'verbose', 'squelch', 'noprompt' }
    s_default_args = { 'url': 'https://dumpling.azurewebsites.net/', 'installpath': os.path.join(os.path.expanduser('~'), '.dumpling') }
    def __init__(self, dictConfig):
        self.__dict__ = copy.copy(DumplingConfig.s_default_args)

        self.Merge(dictConfig)

    @staticmethod
    def Load(strpath):           
        if not os.path.isfile(strpath):
            return None

        try:
            with open(strpath, 'r') as fconfig:
                dict = json.load(fconfig)
                return DumplingConfig(dict)
        except:
            return None

    @staticmethod
    def SaveSettings(strpath, dictSettings):
        config = DumplingConfig.Load(strpath)
        config.Merge(dictSettings)
        config.Save(strpath)

    
    def Merge(self, dictConfig):
        for key, value in dictConfig.iteritems():
            if value or key not in self.__dict__:
                self.__dict__[key] = value

    def Save(self, strpath):
        with open(strpath, 'w') as fconfig:
            json.dump(self._persistable_args(), fconfig, sort_keys=True, indent=4, separators=(',', ': '))  
            Output.Message('configuration saved to %s'%(strpath))

    def _persistable_args(self):
        return dict([(key, value) for key, value in self.__dict__.iteritems() if key not in DumplingConfig.s_unsaved_args and value])

    def __str__(self):
        return _json_format(self._persistable_args())

def _parse_key_value_pair(argStr):
    kvp = string.split(argStr, '=', 1)

    if len(kvp) != 2:
        raise argparse.ArgumentError('the specified property key value pair is invalid. ' + argstr)

if __name__ == '__main__':

    starttime = datetime.datetime.now();

    sharedparser = argparse.ArgumentParser(add_help=False)
    
    sharedparser.add_argument('--verbose', default=False, action='store_true', help='indicates that  all critical, standard, and diagnostic messages should be output')

    sharedparser.add_argument('--squelch', default=False, action='store_true', help='indicates that only critical messages should be ouput')
                                 
    sharedparser.add_argument('--noprompt', default=False, action='store_true', help='suppress prompts for user input')

    sharedparser.add_argument('--logpath', type=str, help='the path to a log file for appending message output')

    sharedparser.add_argument('--url', type=str, help='url of the dumpling service for the connected client')

    sharedparser.add_argument('--configpath', type=str, default=os.path.join(os.path.dirname(os.path.abspath(__file__)), 'dumpling.config.json'), help='path to the saved dumpling client configuration file')

    parser = argparse.ArgumentParser(parents=[sharedparser], description='dumpling client for managing core files and interacting with the dumpling service')
    
    subparsers = parser.add_subparsers(title='command', dest='command')
    
    config_parser = subparsers.add_parser('config', parents=[sharedparser], help='command used for updating saved dumpling client configuration')                               
    
    config_parser.add_argument('action', choices=['dump', 'save', 'clear'], help='dumps the contents of the dumpling client configuration to the console')     
              
    config_parser.add_argument('--dbgpath', type=str, default=None, help='path to debugger to be used by the dumpling client for debugging and triage')
                                        
    config_parser.add_argument('--dbgargs', nargs='*', help='arguments to be passed to the debugger. NOTE: use $(dumppath) as a replacement token for the dumpfile to open in the debugger')

    upload_parser = subparsers.add_parser('upload', parents=[sharedparser], help='command used for uploading dumps and files to the dumpling service')

    upload_parser.add_argument('--dumppath', type=str, help='path to the dumpfile to be uploaded')
                                        
    upload_parser.add_argument('--displayname', type=str, default=None, help='the name to be displayed in reports for the uploaded dump.  This argument is ignored unless --dumppath is specified')

    upload_parser.add_argument('--user', type=str, default=getpass.getuser().lower(), help='The username to pass to the dumpling service.  This argument is ignored unless --dumppath is specified')
    
    upload_parser.add_argument('--suppresstriage', default=False, action='store_true', help='supresses client side triage information from being uploadeded with the dump')

    upload_parser.add_argument('--incpaths', nargs='*', type=str, help='paths to files or directories to be included in the upload')

    upload_parser.add_argument('--properties', nargs='*', type=_parse_key_value_pair, help='a list of properties to be associated with the dump in the format key=value', metavar='key=value')  
                                         
    upload_parser.add_argument('--propfile', type=argparse.FileType('r'), help='path to a file containing a json serialized dictionary of property value paires')

    download_parser = subparsers.add_parser('download', parents=[sharedparser], help='command used for downloading dumps and files from the dumpling service')    
    
    download_idtype = download_parser.add_mutually_exclusive_group(required=True)                                                                                             
    
    download_idtype.add_argument('--dumpid', type=str, help='the dumpling id of the dump to download for debugging')   
    
    download_idtype.add_argument('--hash', type=str, help='the id of the artifact to download')  
   
    download_idtype.add_argument('--symindex', type=str, help='the symstore index of the artifact to download')
                                                                                   
    download_parser.add_argument('--downpath', type=str, help='the path to download the specified content to. NOTE: if both downpath and downdir are specified downdir will be ignored')

    download_parser.add_argument('--downdir', type=str, default=os.getcwd(), help='the path to the directory to download the specified content')    
    
    update_parser = subparsers.add_parser('update', parents=[sharedparser], help='command used for updating dump properties and associated files')
                                                                                                            
    update_parser.add_argument('--dumpid', type=str, help='the dumpling id the specified updates are to be associated with')
    
    update_parser.add_argument('--properties', nargs='*', type=_parse_key_value_pair, help='a list of properties and values to be associated with the dump in the format property=value', metavar='property=value')  
    
    update_parser.add_argument('--propfile', type=argparse.FileType('r'), help='path to a file containing a json serialized dictionary of property value paires')

    update_parser.add_argument('--incpaths', nargs='*', type=str, help='paths to files or directories to be associated with the specified dump')
    
    install_parser = subparsers.add_parser('install', parents=[sharedparser], help='command used for installing dumpling services and support tooling')

    install_parser.add_argument('--debug', default=None, action='store_true', help='indicates that platform specific debugger should be installed on the client') 

    install_parser.add_argument('--triage', default=None, action='store_true', help='indicates that dumpling triage tooling should be installed on the client') 
    
    install_parser.add_argument('--installpath', type=str, help='path to the root directory to install dumpling tooling')

    debug_parser = subparsers.add_parser('debug', parents=[sharedparser], help='download a dumpling dump and load it into the debugger')   
    
    debug_parser.add_argument('--dumpid', type=str, required=True, help='the dumpling id of the dump to download for debugging')   
    
    debug_parser.add_argument('--dbgargs', nargs='*', help='arguments to be passed to the debugger. NOTE: use $(dumppath) as a replacement token for the dumpfile to open in the debugger')

    debug_parser.add_argument('--dbgpath', type=str, default=None, help='path to debugger to be used by the dumpling client for debugging and triage')
                                                 
    debug_parser.add_argument('--downdir', type=str, default=os.getcwd(), help='the path to the directory to download the specified content')    
    
    parsed_args = parser.parse_args()

    config = DumplingConfig.Load(parsed_args.configpath) or DumplingConfig({ })
    config.Merge(parsed_args.__dict__)

    
    Output.s_verbose = config.verbose
    Output.s_squelch = config.squelch
    Output.s_logPath = config.logpath
    Output.s_noprompt = config.noprompt

    DumplingService.s_inst = DumplingService(config.url)
    filequeue = FileTransferManager(DumplingService.s_inst)
    cmdProcesser = CommandProcesser(config, filequeue, DumplingService.s_inst)
    cmdProcesser.Process()

    Output.Message('total elapsed time %s'%(datetime.datetime.now() - starttime))



