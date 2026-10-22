"""Print only a salted admin password hash; stdlib only, no secret CLI arguments."""
import getpass
import hashlib
import secrets

password=getpass.getpass('Новый пароль администратора (не менее 16 символов): ')
confirmation=getpass.getpass('Повторите пароль: ')
if len(password)<16 or password!=confirmation:
    raise SystemExit('Пароли должны совпадать и содержать не менее 16 символов.')
salt=secrets.token_bytes(16)
print(salt.hex()+'$'+hashlib.pbkdf2_hmac('sha256',password.encode(),salt,600_000).hex())
