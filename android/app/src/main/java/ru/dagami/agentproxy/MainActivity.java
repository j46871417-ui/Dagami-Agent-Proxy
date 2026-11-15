package ru.dagami.agentproxy;

import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;
import android.os.Bundle;
import android.text.InputType;
import android.view.View;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import ru.dagami.binding.mobile.Mobile;

public final class MainActivity extends Activity {
    private static final int DIRECT = 0, CUSTOM = 1, DAGAMI = 2;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private SharedPreferences preferences;
    private Spinner mode;
    private EditText address, accessCode;
    private Button check, bind;
    private CheckBox http2;
    private TextView status;

    @Override protected void onCreate(Bundle savedState) {
        super.onCreate(savedState);
        preferences = getSharedPreferences("connection", MODE_PRIVATE);
        ScrollView scroll = new ScrollView(this);
        LinearLayout content = new LinearLayout(this);
        int inset = Math.round(20 * getResources().getDisplayMetrics().density);
        content.setPadding(inset, inset, inset, inset);
        content.setOrientation(LinearLayout.VERTICAL);
        if (android.os.Build.VERSION.SDK_INT >= 30) {
            scroll.setOnApplyWindowInsetsListener((view, insets) -> {
                android.graphics.Insets bars = insets.getInsets(android.view.WindowInsets.Type.systemBars());
                content.setPadding(inset + bars.left, inset + bars.top, inset + bars.right, inset + bars.bottom);
                return insets;
            });
        }
        scroll.addView(content);
        setContentView(scroll);

        TextView title = new TextView(this);
        title.setText("Dagami Agent Proxy");
        title.setTextSize(26);
        content.addView(title);
        TextView description = new TextView(this);
        description.setText("Проверка маршрута для Android. Ключ нужен только для прокси Dagami.");
        content.addView(description);

        mode = new Spinner(this);
        ArrayAdapter<String> options = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item,
                new String[]{"Прямое подключение", "Свой прокси", "Dagami по ключу"});
        options.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        mode.setAdapter(options);
        mode.setContentDescription("Источник прокси");
        content.addView(mode);

        address = privateField("HTTP, HTTPS, SOCKS4/4a, SOCKS5 или socks5s://");
        content.addView(address);
        accessCode = privateField("Ключ прокси из /key");
        content.addView(accessCode);
        http2 = new CheckBox(this);
        http2.setText("HTTP/2 для шлюза Dagami (экспериментально)");
        http2.setChecked(preferences.getBoolean("dagami_http2", false));
        http2.setOnCheckedChangeListener((view, checked) -> preferences.edit().putBoolean("dagami_http2", checked).apply());
        content.addView(http2);
        bind = button(content, "Добавить ключ прокси", this::redeemAccess);
        Button bot = button(content, "Открыть бота", () -> {
            try { startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("https://t.me/bright26bot?start=dagamikey"))); }
            catch (android.content.ActivityNotFoundException ignored) { status.setText("Откройте @bright26bot в Telegram и отправьте /key."); }
        });
        check = button(content, "Проверить маршрут", this::checkRoute);
        status = new TextView(this);
        status.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        status.setText("Проверка выполняется в Dagami. Сеть других приложений работает как обычно.");
        content.addView(status);

        mode.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View item, int position, long id) {
                address.setVisibility(position == CUSTOM ? View.VISIBLE : View.GONE);
                int keyVisibility = position == DAGAMI ? View.VISIBLE : View.GONE;
                accessCode.setVisibility(keyVisibility);
                http2.setVisibility(keyVisibility);
                bind.setVisibility(keyVisibility);
                bot.setVisibility(keyVisibility);
                preferences.edit().putInt("mode", position).apply();
            }
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) { }
        });
        mode.setSelection(Math.max(DIRECT, Math.min(DAGAMI, preferences.getInt("mode", DIRECT))));
    }

    private EditText privateField(String hint) {
        EditText field = new EditText(this);
        field.setHint(hint);
        field.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        field.setSingleLine(true);
        field.setSaveEnabled(false);
        if (android.os.Build.VERSION.SDK_INT >= 26) field.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO);
        return field;
    }

    private Button button(LinearLayout content, String label, Runnable action) {
        Button button = new Button(this);
        button.setText(label);
        button.setOnClickListener(view -> action.run());
        content.addView(button);
        return button;
    }

    private void redeemAccess() {
        String code = accessCode.getText().toString().trim();
        if (code.isEmpty()) { status.setText("Введите ключ прокси из /key."); return; }
        accessCode.getText().clear();
        work("Привязываем устройство…", () -> {
            String endpoint = Mobile.redeemAccess(code, new KeystoreSigner(true));
            if (!preferences.edit().putString("dagami_endpoint", endpoint).commit()) {
                throw new IllegalStateException("Не удалось сохранить адрес шлюза. Повторите привязку.");
            }
            return "Доступ подтверждён. Нажмите «Проверить маршрут».";
        });
    }

    private void checkRoute() {
        int selected = mode.getSelectedItemPosition();
        String proxy = address.getText().toString();
        String endpoint = preferences.getString("dagami_endpoint", "");
        boolean useHTTP2 = http2.isChecked();
        if (selected == DAGAMI && endpoint.isEmpty()) { status.setText("Добавьте ключ прокси Dagami."); return; }
        work("Проверяем соединение…", () -> {
            long code;
            if (selected == DAGAMI) {
                KeystoreSigner signer = new KeystoreSigner(false);
                code = useHTTP2 ? Mobile.checkDagamiHTTP2(endpoint, signer) : Mobile.checkDagami(endpoint, signer);
            }
            else if (selected == CUSTOM) code = Mobile.checkCustom(proxy);
            else code = Mobile.checkDirect();
            return "Проверочный сервер ответил по HTTPS (HTTP " + code + "). Доступ к модели проверяется отдельно.";
        });
    }

    private interface Operation { String run() throws Exception; }

    private void work(String progress, Operation operation) {
        mode.setEnabled(false);
        check.setEnabled(false);
        bind.setEnabled(false);
        http2.setEnabled(false);
        address.setEnabled(false);
        accessCode.setEnabled(false);
        status.setText(progress);
        worker.execute(() -> {
            String result;
            try { result = operation.run(); }
            catch (Exception failure) { result = failure.getMessage() == null ? "Не удалось завершить операцию." : failure.getMessage(); }
            String message = result;
            runOnUiThread(() -> {
                if (isFinishing() || isDestroyed()) return;
                status.setText(message);
                mode.setEnabled(true);
                check.setEnabled(true);
                bind.setEnabled(true);
                http2.setEnabled(true);
                address.setEnabled(true);
                accessCode.setEnabled(true);
            });
        });
    }

    @Override protected void onDestroy() {
        worker.shutdownNow();
        super.onDestroy();
    }
}
