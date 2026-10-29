"""Verify the actual archive preserves audited files, notices and Linux apphost mode."""
from pathlib import Path,PurePosixPath
import argparse,hashlib,json,tarfile

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--archive',type=Path,required=True)
    parser.add_argument('--os',choices=['Windows','Linux'],required=True)
    args=parser.parse_args()
    with tarfile.open(args.archive,'r:gz') as archive:
        members={}
        for entry in archive.getmembers():
            if entry.isdir(): continue
            name=entry.name.removeprefix('./')
            assert entry.isfile() and not PurePosixPath(name).is_absolute() and '..' not in PurePosixPath(name).parts,'Unsafe package entry: '+entry.name
            assert name not in members,'Duplicate package entry: '+name
            members[name]=entry
        def read(name):
            assert name in members,'Missing package file: '+name
            return archive.extractfile(members[name]).read()
        def sha(name): return hashlib.sha256(read(name)).hexdigest()
        for name in ('LICENSE','README.md','ARCHITECTURE.md','CONTRIBUTING.md','docs/MEDIA.md','resources/media/catalog.json','resources/media/provision.py','resources/media/image/worker.cpp','resources/media/sfx/worker.py','provenance/opencaw-extraction.json','provenance/inspection-adaptation.json','notices/bubo/LICENSE','notices/bubo/IMPORT.json','notices/bubo/REUSE.md'):
            assert read(name),'Empty notice/document: '+name
        receipt=json.loads(read('build-info.json'))
        build=receipt['capabilities']['build']
        assert sha('resources/catalog.json')==build['resourceCatalogSha256'],'Catalog identity differs from the verified build'
        catalog=json.loads(read('resources/catalog.json'))
        assert {name for name in members if name.startswith('resources/') and name!='resources/catalog.json'}==set(catalog['files']),'Package has missing/unreviewed resource additions'
        for name,expected in catalog['files'].items():
            assert sha(name)==expected,'Missing/modified audited package resource: '+name
        for name,expected in {**build['assemblies'],**build['launch']}.items():
            assert sha(name)==expected,'Modified package binary/configuration: '+name
        executable='bubo.exe' if args.os=='Windows' else 'bubo'
        assert read(executable)
        if args.os=='Linux': assert members[executable].mode & 0o111,'Linux apphost lost executable mode'
        print('PASS:',len(catalog['files']),'audited resources, launch identities, notices and',args.os,'apphost in',args.archive)

if __name__=='__main__': main()
