import unittest
import dumpling
import sys
import tempfile
import random
import os

DUMPLING_HOSTURL = 'https://dumpling-dev.azurewebsites.net/'

class dumpling_testcase(unittest.TestCase):
    
    def rand_file(self, len = None):
        len = len if len else random.randint(512, 1024 * 16)
        temp = tempfile.mkstemp()
        tempfd = temp[0]
        temppath = temp[1]

        os.write(tempfd, self.rand_bytes(len))
        os.close(tempfd)
        return temppath

    def rand_bytes(self, len):
        rbytes = [ ]
        for i in range(0, len):
            r = random.getrandbits(8) if i % 2 == 0 else 0   
            rbytes.append(r)
        return bytearray(rbytes)

    def _parse_cmdline(self, cmdline):
        return cmdline.split(' ')

class test_dumpling_argparser(dumpling_testcase):
    def test_simple_help(self):
        with self.assertRaises(SystemExit):
            dumpling.main(self._parse_cmdline('dumpling -h'))  
    
    def test_config_help(self):
        with self.assertRaises(SystemExit):
            dumpling.main(self._parse_cmdline('dumpling config -h'))
    
    def test_upload_help(self):
        with self.assertRaises(SystemExit):
            dumpling.main(self._parse_cmdline('dumpling upload -h'))  
  
    def test_update_help(self):
        with self.assertRaises(SystemExit):
            dumpling.main(self._parse_cmdline('dumpling download -h'))

    def test_upload_help(self):
        with self.assertRaises(SystemExit):
            dumpling.main(self._parse_cmdline('dumpling install -h'))

    def test_upload_help(self):
        with self.assertRaises(SystemExit):
            dumpling.main(self._parse_cmdline('dumpling debug -h'))

class test_dumpling_fileutils(dumpling_testcase):
    def test_compress_uncompress(self):
        #create a test file
        origpath = self.rand_file()
        zippedpath = origpath + '.gzip'
        unzippedpath = origpath + '.gunzip'
        
        hash1 = dumpling.FileUtils._hash_and_compress(origpath, zippedpath)
        hash2 = dumpling.FileUtils._hash_and_decompress(zippedpath, unzippedpath)

        size1 = os.path.getsize(origpath)
        size2 = os.path.getsize(unzippedpath)
        zipsize = os.path.getsize(zippedpath)
        
        os.remove(origpath)
        os.remove(zippedpath)
        os.remove(unzippedpath)

        self.assertEqual(hash1, hash2)
        self.assertEqual(size1, size2)
        self.assertTrue(zipsize < size1)

class test_dumpling_filetransfer(dumpling_testcase):
    def test_upload_download_artifact(self):
        origpath = self.rand_file()

        copypath = origpath + '.down'
        try:
            dumpsvc = dumpling.DumplingService(DUMPLING_HOSTURL)
        
            transmgr = dumpling.FileTransferManager(dumpsvc)

            hash = transmgr.QueueFileUpload(None, origpath).await_result()

            transmgr.QueueFileDownload(hash, copypath).await_result()

            self.assertEqual(os.path.getsize(origpath), os.path.getsize(copypath))
        finally:
            dumpling.FileUtils._try_remove(origpath)

            dumpling.FileUtils._try_remove(copypath)

        


if __name__ == '__main__':
    dumpling.Output.s_quiet = True
    unittest.main(verbosity=2)
