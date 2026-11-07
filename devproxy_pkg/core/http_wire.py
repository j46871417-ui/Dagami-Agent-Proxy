"""One framed plain HTTP transaction per socket; CONNECT remains opaque."""
import re
import urllib.parse
import threading


def fields(header):
    result = []
    for line in header.split(b'\r\n')[1:]:
        if not line:
            break
        name, separator, value = line.partition(b':')
        if not separator or not re.fullmatch(rb"[!#$%&'*+\-.^_`|~0-9A-Za-z]+", name):
            raise ValueError('Invalid HTTP header')
        result.append((name, value.strip()))
    return result


def value(items, name):
    matches = [v for k, v in items if k.lower() == name]
    if len(matches) > 1:
        raise ValueError('Duplicate HTTP framing header')
    return matches[0] if matches else None


def framing(items):
    length, transfer = value(items, b'content-length'), value(items, b'transfer-encoding')
    if transfer is not None:
        if length is not None or transfer.lower() != b'chunked':
            raise ValueError('Ambiguous HTTP body framing')
        return -2
    if length is None:
        return -1
    if not re.fullmatch(rb'[0-9]{1,19}', length) or int(length) > 2**63 - 1:
        raise ValueError('Invalid Content-Length')
    return int(length)


def request_headers(header, target, absolute, authorization=None):
    items = fields(header)
    framing(items)
    expect = value(items, b'expect')
    if expect is not None and expect.lower() != b'100-continue':
        raise ValueError('Unsupported Expect header')
    first = header.split(b'\r\n', 1)[0].split(b' ')
    if len(first) != 3:
        raise ValueError('Invalid HTTP request')
    parsed = urllib.parse.urlsplit(target)
    if parsed.scheme not in ('http', 'https') or not parsed.hostname or parsed.username is not None or parsed.fragment:
        raise ValueError('Invalid HTTP destination')
    path = urllib.parse.urlunsplit(('', '', parsed.path or '/', parsed.query, ''))
    first[1] = (target if absolute else path).encode('latin-1')
    result = [b' '.join(first), b'Host: ' + parsed.netloc.encode('ascii')]
    dropped = (b'host', b'connection', b'proxy-connection', b'proxy-authorization', b'expect')
    result.extend(k + b': ' + v for k, v in items if k.lower() not in dropped)
    if authorization:
        result.append(b'Proxy-Authorization: ' + authorization)
    result.append(b'Connection: Upgrade' if value(items, b'upgrade') is not None else b'Connection: close')
    return b'\r\n'.join(result) + b'\r\n\r\n'


class Reader:
    def __init__(self, sock, initial=b''):
        self.sock, self.initial = sock, memoryview(initial)

    def read(self, size):
        if self.initial:
            chunk = self.initial[:size].tobytes()
            self.initial = self.initial[len(chunk):]
            return chunk
        return self.sock.recv(size)

    def line(self, limit=8192):
        result = bytearray()
        while len(result) < limit:
            chunk = self.read(1)
            if not chunk:
                raise ConnectionError('Truncated HTTP framing')
            result.extend(chunk)
            if result.endswith(b'\r\n'):
                return bytes(result)
        raise ValueError('HTTP line too large')

    def header(self):
        result = bytearray()
        while len(result) < 65536:
            line = self.line()
            result.extend(line)
            if line == b'\r\n':
                return bytes(result)
        raise ValueError('HTTP headers too large')


def exact(reader, output, length):
    while length:
        chunk = reader.read(min(16384, length))
        if not chunk:
            raise ConnectionError('Truncated HTTP body')
        output.sendall(chunk)
        length -= len(chunk)


def body(reader, output, frame, eof=False):
    if frame >= 0:
        exact(reader, output, frame)
    elif frame == -2:
        while True:
            line = reader.line()
            number = line[:-2].split(b';', 1)[0]
            if not re.fullmatch(rb'[0-9A-Fa-f]{1,16}', number) or int(number, 16) > 2**63 - 1:
                raise ValueError('Invalid HTTP chunk')
            size = int(number, 16)
            output.sendall(line)
            if not size:
                total = 0
                while True:
                    trailer = reader.line()
                    total += len(trailer)
                    if total > 65536:
                        raise ValueError('HTTP trailers too large')
                    output.sendall(trailer)
                    if trailer == b'\r\n':
                        return
            exact(reader, output, size)
            if reader.line(2) != b'\r\n':
                raise ValueError('Invalid HTTP chunk terminator')
            output.sendall(b'\r\n')
    elif eof:
        while True:
            chunk = reader.read(16384)
            if not chunk:
                return
            output.sendall(chunk)


def exchange(client, upstream, original, forwarded, initial, started, relay):
    items = fields(original)
    request = Reader(client, initial)
    upstream.sendall(forwarded)
    if value(items, b'expect') is not None:
        started()
        client.sendall(b'HTTP/1.1 100 Continue\r\n\r\n')
    request_frame = framing(items)
    sender = None
    if request_frame > 0 or request_frame == -2:
        def upload():
            try:
                body(request, upstream, request_frame)
            except (OSError, ValueError, ConnectionError):
                try:
                    upstream.shutdown(2)
                except OSError:
                    pass
                upstream.close()
        sender = threading.Thread(target=upload, daemon=True)
        sender.start()
    upstream.settimeout(None)
    response = Reader(upstream)
    for _ in range(32):
        header = response.header()
        first = header.split(b'\r\n', 1)[0]
        status = first.split(b' ')
        if len(status) < 2 or status[0] not in (b'HTTP/1.0', b'HTTP/1.1') or not re.fullmatch(rb'[1-5][0-9]{2}', status[1]):
            raise ValueError('Invalid HTTP response')
        code = int(status[1])
        response_fields = fields(header)
        frame = framing(response_fields)
        if code == 101:
            if value(items, b'upgrade') is None:
                raise ValueError('Unsolicited HTTP upgrade')
            if sender is not None:
                sender.join(5)
                if sender.is_alive():
                    raise ValueError('Incomplete HTTP upgrade request')
            started()
            client.sendall(header)
            # The reader has no read-ahead; preserve any client upgrade bytes.
            if request.initial:
                upstream.sendall(request.initial)
            relay(client, upstream)
            return
        lines = [first] + [k + b': ' + v for k, v in response_fields if k.lower() not in (b'connection', b'proxy-connection')]
        if code >= 200:
            lines.append(b'Connection: close')
        started()
        client.sendall(b'\r\n'.join(lines) + b'\r\n\r\n')
        if code < 200:
            continue
        if not original.startswith(b'HEAD ') and code not in (204, 304):
            body(response, client, frame, eof=True)
        return
    raise ValueError('Too many informational responses')
