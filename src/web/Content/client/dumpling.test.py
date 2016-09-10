import unittest
import dumpling
import sys
import tempfile
import random
import os

class dumpling_testcase(unittest.TestCase):
    def dumpling_exec(self, strcmd):
        strcmd.split(' ')

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

class test_dumpling_argparser(dumpling_testcase):
    def test_simple_help(self):
        self.dumpling_exec('-h')   
    
    def test_config_help(self):
        self.dumpling_exec('config -h')
    
    def test_upload_help(self):
        self.dumpling_exec('upload -h')  
  
    def test_update_help(self):
        self.dumpling_exec('download -h')

    def test_upload_help(self):
        self.dumpling_exec('install -h')

    def test_upload_help(self):
        self.dumpling_exec('debug -h')

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

if __name__ == '__main__':
    unittest.main(verbosity=2)
