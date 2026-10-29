"""Validate the reviewed runtime catalog; normal builds never bless changed files."""
from pathlib import Path
import hashlib
import json
import re

ROOT=Path(__file__).resolve().parents[1]
catalog=ROOT/'resources/catalog.json'
expected=re.search(r'ExpectedSha256 = "([a-f0-9]{64})"', (ROOT/'src/Bubo.Cli/RuntimeCatalog.cs').read_text(encoding='utf-8')).group(1)
assert hashlib.sha256(catalog.read_bytes()).hexdigest()==expected, 'Audited catalog changed; review source and update its compiled identity explicitly.'
value=json.loads(catalog.read_text(encoding='utf-8'))
assert value['schemaVersion']==1 and value['files']
actual={path.relative_to(ROOT).as_posix() for path in (ROOT/'resources').rglob('*') if path.is_file() and path!=catalog and '__pycache__' not in path.parts and path.suffix!='.pyc'}
assert actual==set(value['files']), 'Resource inventory has missing/unreviewed additions; review and register exact bytes explicitly.'
for relative,digest in value['files'].items():
    path=ROOT/relative
    assert path.resolve().is_relative_to((ROOT/'resources').resolve()) and not path.is_symlink(), 'Resource escape/link: '+relative
    assert path.is_file() and hashlib.sha256(path.read_bytes()).hexdigest()==digest, 'Modified/missing audited resource: '+relative
print('PASS:',len(value['files']),'audited runtime resources; compiled catalog identity matches.')
