import http.client
import importlib.util
import json
from pathlib import Path
import tempfile
import threading
import time
import unittest

spec = importlib.util.spec_from_file_location('pool_admin', Path(__file__).with_name('pool_admin.py'))
pool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pool)


class InventoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / 'base-routes.json').write_text(json.dumps({'source_ip': '1.1.1.1', 'routes': [{'name': 'own', 'proxy': ''}]}))
        self.inventory = pool.Inventory(self.root)

    def test_expiry_removes_credentials_and_route(self):
        identifier = self.inventory.save({'label': 'Fixture', 'url': 'https://fixture:PRIVATE@8.8.8.8:443', 'days': '2'})
        data = json.loads((self.root / 'routes.json').read_text())
        self.assertEqual(data['routes'][-1]['name'], identifier)
        self.assertTrue(data['routes'][-1]['require_foreign'])
        self.inventory.publish(time.time() + 3 * 86400)
        self.assertEqual(self.inventory.rows(), [])
        self.assertNotIn('PRIVATE', (self.root / 'routes.json').read_text())
        self.assertNotIn(b'PRIVATE', (self.root / 'providers.db').read_bytes())

    def test_disable_enable_and_delete(self):
        identifier = self.inventory.save({'label': 'Fixture', 'url': 'socks5://8.8.8.8:1080'})
        self.inventory.change({'id': identifier, 'action': 'disable'})
        self.assertEqual(len(json.loads((self.root / 'routes.json').read_text())['routes']), 1)
        self.inventory.change({'id': identifier, 'action': 'enable'})
        self.assertEqual(len(json.loads((self.root / 'routes.json').read_text())['routes']), 2)
        self.inventory.change({'id': identifier, 'action': 'delete'})
        self.assertEqual(self.inventory.rows(), [])

    def test_separate_fields_and_priority_mode(self):
        identifier = self.inventory.save({'label': 'Fixture', 'protocol': 'socks5', 'host': '8.8.8.8', 'port': '1080', 'username': 'name@host', 'password': 'pass:/word'})
        self.assertEqual(self.inventory.rows()[0]['url'], 'socks5://name%40host:pass%3A%2Fword@8.8.8.8:1080')
        self.inventory.preference('yes')
        self.assertEqual(json.loads((self.root / 'routes.json').read_text())['routes'][0]['name'], identifier)
        self.inventory.preference('no')
        self.assertEqual(json.loads((self.root / 'routes.json').read_text())['routes'][0]['name'], 'own')

    def test_expiry_inputs_and_validation_do_not_expose_credentials(self):
        self.assertEqual(pool.expiry('2', now=10), 172810)
        self.assertEqual(pool.expiry('', '2026-12-18T14:40', now=0), pool.dt.datetime(2026,12,18,9,40,tzinfo=pool.UTC).timestamp())
        for address in ('http://secret@127.0.0.1:80', 'http://secret@100.64.0.1:80', 'file://secret@8.8.8.8:80', 'https://secret@8.8.8.8:443?x=1'):
            with self.assertRaises(ValueError) as raised:
                pool.proxy_url(address)
            self.assertNotIn('secret', str(raised.exception))
        with self.assertRaises(ValueError):
            pool.expiry('1', '2030-01-01')

    def test_dashboard_never_contains_provider_details_and_requires_auth(self):
        self.inventory.save({'label': '<script>fixture</script>', 'url': 'https://user:PRIVATE@8.8.8.8:443'})
        server = pool.ThreadingHTTPServer(('127.0.0.1', 0), pool.Handler)
        server.inventory = self.inventory
        server.password, server.session, server.csrf = 'PASS', 'SESSION', 'CSRF'
        server.health_file = self.root / 'health.json'
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        self.addCleanup(server.server_close); self.addCleanup(server.shutdown)
        client = http.client.HTTPConnection('127.0.0.1', server.server_port)
        self.addCleanup(client.close)
        client.request('GET', '/')
        response = client.getresponse(); content = response.read().decode()
        self.assertNotIn('8.8.8.8', content)
        self.assertIn('Пароль панели', content)
        client.request('GET', '/', headers={'Cookie': 'dagami_pool_owner_v2=SESSION'})
        response = client.getresponse(); content = response.read().decode()
        self.assertNotIn('PRIVATE', content); self.assertNotIn('8.8.8.8', content)
        self.assertIn('&lt;script&gt;', content)
        client.request('POST', '/change', 'id=x&action=delete', {'Content-Type': 'application/x-www-form-urlencoded', 'Cookie': 'dagami_pool_owner_v2=SESSION'})
        response = client.getresponse(); response.read()
        self.assertEqual(response.status, 409)
        client.request('GET', '/', headers={'Host': 'evil.test'})
        response = client.getresponse(); response.read()
        self.assertEqual(response.status, 404)
        client.request('POST', '/login', 'password=PASS&csrf=CSRF', {'Content-Type': 'application/x-www-form-urlencoded', 'Origin': 'null'})
        response = client.getresponse(); content = response.read().decode()
        self.assertEqual(response.status, 200)
        self.assertIn('dagami_pool_owner_v2=SESSION', response.getheader('Set-Cookie'))
        self.assertIn('Вход выполнен', content)
        client.request('POST', '/change', 'id=x&action=delete&csrf=CSRF', {'Content-Type': 'application/x-www-form-urlencoded', 'Cookie': 'dagami_pool_owner_v2=OLD_SESSION'})
        response = client.getresponse(); content = response.read().decode()
        self.assertEqual(response.status, 401)
        self.assertIn('Войдите заново', content)
        self.assertNotIn('Запрещено', content)


if __name__ == '__main__':
    unittest.main()
