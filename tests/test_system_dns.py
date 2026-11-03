"""Unix DNS fixtures. Never call a system DNS command or a public endpoint."""
import base64
import copy
import json
import os
from pathlib import Path
import socket
import struct
import tempfile
import unittest
from unittest.mock import patch
from devproxy_pkg import system_dns as dns


def request(action='apply', **values):
    result=dict(action=action, job='a'*32, server='185.149.145.6', mode='adapters',
                preset='Development', services=['wifi'], confirmed=True)
    result.update(values);return result


class FakeSystem(dns.Backend):
    def __init__(self):
        self.values={'wifi':['10.0.0.1'], 'other':['10.0.0.2']}
        self.writes=[];self.fail=None;self.gone=set();self.unsupported=False
    def validate(self, req):
        if self.unsupported:raise RuntimeError('unsupported')
    def missing(self, change):return change['id'] in self.gone
    def read_dns(self, identity):return copy.deepcopy(self.values[identity])
    def write_dns(self, identity, value):
        self.writes.append((identity,copy.deepcopy(value)))
        if self.fail==identity:raise RuntimeError('write failed '+identity)
        self.values[identity]=copy.deepcopy(value)
    def plan(self, req):
        return [dict(kind='dns',id=item,before=self.read_dns(item),after=[req['server']]) for item in req['services']]
    def reset_plan(self):
        return [dict(kind='dns',id=item,before=self.read_dns(item),after=[]) for item in self.values]
    def flush(self):pass


class FakeProbes:
    def __init__(self):self.calls=[];self.failure=None
    def baseline(self):
        self.calls.append('baseline')
        if self.failure=='baseline':raise RuntimeError('no baseline')
        return ['example.com','www.microsoft.com']
    def target(self, server, hosts):
        self.calls.append('target')
        if self.failure=='target':raise RuntimeError('bad target')
    def verify(self, hosts):
        self.calls.append('verify')
        if self.failure in ('after','recovery'):
            if self.failure=='after':self.failure=None
            raise RuntimeError('no network')


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.store=dns.Store(Path(self.temp.name).resolve()/'backup',enforce_owner=False)
        self.system=FakeSystem();self.probes=FakeProbes()
        self.engine=dns.Engine(self.system,self.store,self.probes)
    def test_apply_and_exact_restore(self):
        self.engine.perform(request())
        self.assertEqual(['185.149.145.6'],self.system.values['wifi'])
        self.assertEqual('Applied',self.store.load()['status'])
        self.engine.perform(request('restore'))
        self.assertEqual(['10.0.0.1'],self.system.values['wifi'])
        self.assertEqual('Restored',self.store.load()['status'])
    def test_backup_must_finish_before_first_write(self):
        with patch.object(self.store,'save',side_effect=OSError('locked')):
            with self.assertRaisesRegex(OSError,'locked'):self.engine.perform(request())
        self.assertFalse(self.system.writes)
    def test_baseline_and_target_failure_leave_current_dns_and_backup(self):
        self.engine.perform(request());before=self.store.load()
        for stage in ('baseline','target'):
            self.probes.failure=stage;self.system.writes.clear()
            with self.assertRaises(RuntimeError):self.engine.perform(request(job='b'*32))
            self.assertFalse(self.system.writes);self.assertEqual(before,self.store.load())
    def test_unsupported_mode_does_not_undo_previous_apply(self):
        self.engine.perform(request());self.system.unsupported=True;self.system.writes.clear()
        with self.assertRaisesRegex(RuntimeError,'unsupported'):self.engine.perform(request(job='b'*32))
        self.assertFalse(self.system.writes)
    def test_postcheck_failure_restores_then_checks_recovery(self):
        self.probes.failure='after'
        with self.assertRaisesRegex(RuntimeError,'после отката проверены'):self.engine.perform(request())
        self.assertEqual(['10.0.0.1'],self.system.values['wifi'])
        self.assertEqual(['baseline','target','verify','verify'],self.probes.calls)
    def test_does_not_claim_network_recovered_when_probe_fails(self):
        self.probes.failure='recovery'
        with self.assertRaisesRegex(RuntimeError,'не подтверждён'):self.engine.perform(request())
        self.assertEqual(['10.0.0.1'],self.system.values['wifi'])
    def test_foreign_edits_and_removed_services_release_backup(self):
        self.engine.perform(request(services=['wifi','other']))
        self.system.values['wifi']=['9.9.9.9'];self.system.gone={'other'}
        result=self.engine.perform(request('restore'))
        self.assertIn('Пропуски',result);self.assertEqual('Released',self.store.load()['status'])
        self.assertEqual(['9.9.9.9'],self.system.values['wifi'])
        self.assertEqual(2,len(self.store.load()['warnings']))
    def test_reapply_retains_original_and_archives_last_backup(self):
        self.engine.perform(request());self.engine.perform(request(job='b'*32,server='1.1.1.1'))
        self.assertTrue((self.store.root/('history-'+'a'*32+'.json')).is_file())
        self.engine.perform(request('restore'));self.assertEqual(['10.0.0.1'],self.system.values['wifi'])
    def test_failed_second_write_restores_first(self):
        self.system.fail='other'
        with self.assertRaisesRegex(RuntimeError,'после отката проверены'):self.engine.perform(request(services=['wifi','other']))
        self.assertEqual(['10.0.0.1'],self.system.values['wifi'])
    def test_reset_continues_after_one_error_and_keeps_snapshot(self):
        self.system.fail='wifi'
        with self.assertRaisesRegex(RuntimeError,'частично'):self.engine.perform(request('reset'))
        self.assertEqual([],self.system.values['other'])
        self.assertEqual(['10.0.0.1'],self.store.load()['changes'][0]['before'])
        self.assertEqual('ClearFailed',self.store.load()['status'])
    def test_apply_after_reset_never_resurrects_cleared_dns(self):
        self.engine.perform(request('reset'));self.system.writes.clear()
        self.engine.perform(request(job='b'*32));self.engine.perform(request('restore'))
        self.assertEqual([],self.system.values['wifi'])
        self.assertNotIn(('wifi',['10.0.0.1']),self.system.writes)
    def test_restore_reset_returns_original_values(self):
        self.engine.perform(request('reset'));self.engine.perform(request('restore'))
        self.assertEqual(['10.0.0.1'],self.system.values['wifi'])
        self.assertEqual(['10.0.0.2'],self.system.values['other'])
    def test_restore_without_backup_does_not_mutate(self):
        with self.assertRaisesRegex(RuntimeError,'Копии'):self.engine.perform(request('restore'))
        self.assertFalse(self.system.writes)
    def test_store_serializes_operations(self):
        with self.store.locked():
            with self.assertRaises((OSError,RuntimeError)):
                with self.store.locked():self.fail('lock acquired twice')
    def test_status_does_not_expose_backup(self):
        self.engine.perform(request());value=json.loads((self.store.root/'status.json').read_text())
        self.assertEqual({'status','warnings','errors'},set(value))


UUID='11111111-2222-3333-4444-555555555555'


class NetworkManagerTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.config=Path(self.temp.name).resolve();self.calls=[];self.plugin='dnsmasq'
        def execute(args):
            self.calls.append(args)
            if args[0]=='NetworkManager':return '[main]\ndns='+self.plugin+'\n'
            if '-g' in args:
                prop=args[args.index('-g')+1]
                return {'dns':'2001:4860:4860::8888' if prop.startswith('ipv6') else '10.0.0.1','dns-search':'corp.example','ignore-auto-dns':'no'}[prop.split('.',1)[1]]
            if 'show' in args:return UUID+':802-11-wireless:wlan0\n'+'a'*36+':loopback:lo\n'
            return ''
        self.backend=dns.LinuxBackend(execute,config=self.config,nmcli='nmcli',manager='NetworkManager')
    def test_split_uses_only_scoped_dns_and_reload(self):
        plan=self.backend.plan(request(mode='domains'));self.assertEqual(1,len(plan))
        text=base64.b64decode(plan[0]['after']['data']).decode()
        self.assertIn('server=/chatgpt.com/185.149.145.6',text)
        self.assertNotIn('server=/#/',text);self.backend.flush()
        self.assertEqual(['nmcli','general','reload','dns-full'],self.calls[-1])
    def test_other_dns_plugins_rejected_without_mutation(self):
        for plugin in ('systemd-resolved','default','none',''):
            self.plugin=plugin
            with self.assertRaisesRegex(RuntimeError,'dnsmasq'):self.backend.plan(request(mode='domains'))
        self.assertFalse(any('modify' in args for args in self.calls))
        self.assertFalse(list(self.config.iterdir()))
    def test_foreign_split_rule_conflict_not_overwritten(self):
        path=self.config/'foreign.conf';path.write_text('server=/chatgpt.com/9.9.9.9\n')
        with self.assertRaisesRegex(RuntimeError,'внешнее'):self.backend.plan(request(mode='domains',replace=True))
        self.assertEqual('server=/chatgpt.com/9.9.9.9\n',path.read_text())
    def test_foreign_multi_domain_directive_is_also_a_conflict(self):
        (self.config/'foreign.conf').write_text('server = /unrelated.example/.chatgpt.com/9.9.9.9#53\n')
        with self.assertRaisesRegex(RuntimeError,'внешнее'):self.backend.plan(request(mode='domains'))
    def test_general_dns_preserves_other_family_and_search(self):
        plan=self.backend.plan(request(services=[UUID]));self.assertEqual(UUID+'/4',plan[0]['id'])
        self.assertEqual('corp.example',plan[0]['after']['dns-search'])
        self.backend.write(plan[0],plan[0]['after'])
        mutation=next(args for args in self.calls if 'modify' in args)
        self.assertNotIn('ipv6.dns',mutation);self.assertIn('ipv4.ignore-auto-dns',mutation)
        self.assertIn(['nmcli','device','reapply','wlan0'],self.calls)
        self.assertFalse(any('down' in args or 'up' in args for args in self.calls))
    def test_ipv6_query_not_escaped_and_write_only_ipv6(self):
        plan=self.backend.plan(request(services=[UUID],server='2606:4700:4700::1111'))
        self.assertEqual(UUID+'/6',plan[0]['id'])
        self.assertEqual('2001:4860:4860::8888',plan[0]['before']['dns'])
        for args in self.calls:
            if '-g' in args:self.assertEqual('no',args[args.index('--escape')+1])
    def test_unknown_service_rejected_before_writes(self):
        with self.assertRaises(ValueError):self.backend.plan(request(services=['bad']))
        self.assertFalse(any('modify' in args for args in self.calls))
    def test_reset_keeps_non_dns_directives_and_snapshot(self):
        path=self.config/'foreign.conf';raw=b'# note\nserver=/corp/10.0.0.1\ndhcp-range=1,2\naddress=/ads/0.0.0.0\n';path.write_bytes(raw)
        plan=self.backend.reset_plan();self.assertEqual(3,len(plan))
        file=plan[-1];self.assertEqual(raw,base64.b64decode(file['before']['data']))
        self.assertEqual(b'# note\ndhcp-range=1,2\n',base64.b64decode(file['after']['data']))
        self.assertEqual({'dns':'','ignore-auto-dns':'no','dns-search':''},plan[0]['after'])


class MacTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.root=Path(self.temp.name).resolve();self.calls=[]
        def execute(args):
            self.calls.append(args)
            if '-listallnetworkservices' in args:return 'An asterisk denotes disabled.\nWi-Fi\n*USB LAN\n'
            if '-getdnsservers' in args:return '10.0.0.1\n2001:4860:4860::8888' if args[-1]=='Wi-Fi' else "There aren't any DNS Servers set on USB LAN."
            return ''
        self.backend=dns.MacBackend(execute,self.root/'resolver')
    def test_services_strip_disabled_marker_and_auto_is_empty(self):
        self.assertEqual(['Wi-Fi','USB LAN'],[item['id'] for item in self.backend.services()])
        self.assertEqual([],self.backend.read_dns('USB LAN'))
    def test_service_argument_and_other_family_preserved(self):
        plan=self.backend.plan(request(services=['Wi-Fi']))
        self.assertEqual(['2001:4860:4860::8888','185.149.145.6'],plan[0]['after'])
        self.backend.write(plan[0],plan[0]['after'])
        self.assertEqual(['/usr/sbin/networksetup','-setdnsservers','Wi-Fi','2001:4860:4860::8888','185.149.145.6'],self.calls[-1])
    def test_domain_rule_and_foreign_file_exact_restore(self):
        self.backend.resolver.mkdir();path=self.backend.resolver/'chatgpt.com';raw=b'# external\nnameserver 9.9.9.9\n';path.write_bytes(raw)
        with self.assertRaisesRegex(RuntimeError,'внешнее'):self.backend.plan(request(mode='domains'))
        plan=self.backend.plan(request(mode='domains',replace=True))
        change=next(c for c in plan if c['id']==str(path));self.backend.write(change,change['after'])
        self.assertIn('185.149.145.6',path.read_text());self.backend.write(change,change['before'])
        self.assertEqual(raw,path.read_bytes())
    def test_reset_uses_automatic_dns_and_backs_up_foreign_resolver(self):
        self.backend.resolver.mkdir();(self.backend.resolver/'corp').write_text('nameserver 10.0.0.1\n')
        plan=self.backend.reset_plan();self.assertEqual(3,len(plan))
        self.backend.write(plan[0],plan[0]['after'])
        self.assertEqual('Empty',self.calls[-1][-1]);self.assertFalse(plan[-1]['after']['exists'])
    def test_unknown_service_and_bad_dns_rejected(self):
        with self.assertRaises(ValueError):self.backend.plan(request(services=['no']))
        self.backend.execute=lambda _: 'this is not an IP'
        with self.assertRaises(ValueError):self.backend.read_dns('Wi-Fi')


class ValidationTests(unittest.TestCase):
    def test_bad_requests_and_scope_injection_rejected(self):
        for bad in (dict(action='wrong'),request(job='../bad'),request(server='1.1.1.1\n'),
                    request(server='fe80::1%en0\nnameserver 1.1.1.1'),request(services=[{}]),request(mode='other'),request('reset',confirmed=False)):
            with self.assertRaises((ValueError,TypeError)):dns.validate_request(bad)
    def test_domain_presets_match_windows(self):
        self.assertEqual(75,len(dns.domains()));self.assertEqual(85,len(dns.domains('Full')))
        self.assertIn('clients6.google.com',dns.domains());self.assertNotIn('twitch.tv',dns.domains())
        self.assertIn('twitch.tv',dns.domains('Full'))
    def test_snapshot_exact_bytes_and_size_limit(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp).resolve()/'rule';path.write_bytes(b'\xff\r\n')
            value=dns.file_snapshot(path);self.assertEqual(b'\xff\r\n',base64.b64decode(value['data']))
            path.write_bytes(b'0'*65537)
            with self.assertRaises(RuntimeError):dns.file_snapshot(path)
    def test_symlinks_never_followed(self):
        with tempfile.TemporaryDirectory() as temp:
            path=Path(temp)/'rule';target=Path(temp)/'target';target.write_text('external')
            try:path.symlink_to(target)
            except OSError:self.skipTest('symlink privilege unavailable')
            with self.assertRaises(RuntimeError):dns.file_snapshot(path)
            with self.assertRaises(RuntimeError):dns.atomic(path,b'overwrite')
            self.assertEqual('external',target.read_text())
    def test_dns_packet_checks_and_compressed_answer(self):
        class UDP:
            invalid=False
            def __init__(self,*_):pass
            def __enter__(self):return self
            def __exit__(self,*_):pass
            def settimeout(self,*_):pass
            def connect(self,*_):pass
            def send(self,packet):self.packet=packet
            def recv(self,*_):
                packet=self.packet;kind=struct.unpack('!H',packet[-4:-2])[0]
                address=socket.inet_pton(socket.AF_INET if kind==1 else socket.AF_INET6,'1.1.1.1' if kind==1 else '2606:4700:4700::1111')
                return (b'xx' if self.invalid else packet[:2])+b'\x81\x80\x00\x01\x00\x01'+b'\0'*4+packet[12:]+b'\xc0\x0c'+struct.pack('!HHIH',kind,1,60,len(address))+address
        with patch.object(dns.socket,'socket',UDP):
            self.assertEqual(['1.1.1.1','2606:4700:4700::1111'],dns.dns_addresses('example.com','1.1.1.1'))
            UDP.invalid=True;self.assertEqual([],dns.dns_addresses('example.com','1.1.1.1'))


if __name__=='__main__':unittest.main()
