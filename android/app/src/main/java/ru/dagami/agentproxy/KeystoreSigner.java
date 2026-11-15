package ru.dagami.agentproxy;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;

import java.math.BigInteger;
import java.security.KeyPairGenerator;
import java.security.KeyStore;
import java.security.Signature;
import java.security.interfaces.ECPublicKey;
import java.security.spec.ECGenParameterSpec;

import ru.dagami.binding.mobile.DeviceSigner;

final class KeystoreSigner implements DeviceSigner {
    private static final String PROVIDER = "AndroidKeyStore";
    private static final String ALIAS = "ru.dagami.agentproxy.device-p256.v1";
    private final KeyStore.PrivateKeyEntry entry;
    private final byte[] point = new byte[65];

    KeystoreSigner(boolean create) throws Exception {
        entry = loadEntry(create);
        ECPublicKey key = (ECPublicKey) entry.getCertificate().getPublicKey();
        point[0] = 4;
        writeCoordinate(key.getW().getAffineX(), 1);
        writeCoordinate(key.getW().getAffineY(), 33);
    }

    private static synchronized KeyStore.PrivateKeyEntry loadEntry(boolean create) throws Exception {
        KeyStore store = KeyStore.getInstance(PROVIDER);
        store.load(null);
        if (create && !store.containsAlias(ALIAS)) {
            KeyPairGenerator generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, PROVIDER);
            generator.initialize(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN)
                    .setAlgorithmParameterSpec(new ECGenParameterSpec("secp256r1"))
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build());
            generator.generateKeyPair();
        }
        KeyStore.Entry stored = store.getEntry(ALIAS, null);
        if (!(stored instanceof KeyStore.PrivateKeyEntry)) {
            throw new IllegalStateException("Ключ устройства недоступен. Повторите привязку прокси.");
        }
        return (KeyStore.PrivateKeyEntry) stored;
    }

    private void writeCoordinate(BigInteger value, int offset) {
        byte[] magnitude = value.toByteArray();
        int start = magnitude.length > 1 && magnitude[0] == 0 ? 1 : 0;
        int length = magnitude.length - start;
        if (value.signum() < 0 || length > 32) throw new IllegalArgumentException("Некорректный ключ P-256");
        System.arraycopy(magnitude, start, point, offset + 32 - length, length);
    }

    @Override public byte[] publicPoint() { return point.clone(); }

    @Override public byte[] signDER(byte[] message) throws Exception {
        Signature signature = Signature.getInstance("SHA256withECDSA");
        signature.initSign(entry.getPrivateKey());
        signature.update(message);
        return signature.sign();
    }
}
