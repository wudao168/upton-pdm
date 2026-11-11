package com.upton.pdm.production;

import android.app.Activity;
import android.content.Intent;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.*;
import android.widget.*;
import com.google.zxing.*;
import com.google.zxing.common.HybridBinarizer;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;

@SuppressWarnings("deprecation")
public final class ScanActivity extends Activity implements SurfaceHolder.Callback {
    private Camera camera;
    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private final AtomicBoolean decoding = new AtomicBoolean();
    private volatile boolean finished;
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        LinearLayout layout = new LinearLayout(this); layout.setOrientation(LinearLayout.VERTICAL); layout.setBackgroundColor(0xff10291d);
        TextView title = new TextView(this); title.setText("对准图纸或包装二维码"); title.setTextColor(-1); title.setTextSize(22); title.setGravity(Gravity.CENTER); layout.addView(title, new LinearLayout.LayoutParams(-1, dp(56)));
        SurfaceView surface = new SurfaceView(this); layout.addView(surface, new LinearLayout.LayoutParams(-1, 0, 1)); surface.getHolder().addCallback(this);
        Button back = new Button(this); back.setText("返回扫码键输入"); back.setTextSize(20); back.setOnClickListener(v -> finish()); layout.addView(back, new LinearLayout.LayoutParams(-1, dp(64)));
        setContentView(layout);
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    @Override public void surfaceCreated(SurfaceHolder holder) {
        try {
            camera = Camera.open(); Camera.Parameters parameters = camera.getParameters();
            if (parameters.getSupportedFocusModes().contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE)) parameters.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            camera.setParameters(parameters); camera.setDisplayOrientation(90); camera.setPreviewDisplay(holder);
            camera.setPreviewCallback((bytes, source) -> {
                if (finished || !decoding.compareAndSet(false, true)) return;
                Camera.Size size = source.getParameters().getPreviewSize();
                byte[] copy = Arrays.copyOf(bytes, bytes.length);
                decoder.execute(() -> {
                    try {
                        MultiFormatReader reader = new MultiFormatReader();
                        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
                        hints.put(DecodeHintType.POSSIBLE_FORMATS, Collections.singletonList(BarcodeFormat.QR_CODE));
                        hints.put(DecodeHintType.CHARACTER_SET, "UTF-8");
                        Result result = reader.decode(new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(copy, size.width, size.height, 0, 0, size.width, size.height, false))), hints);
                        finished = true;
                        runOnUiThread(() -> { setResult(RESULT_OK, new Intent().putExtra("code", result.getText())); finish(); });
                    } catch (NotFoundException ignored) { } finally { decoding.set(false); }
                });
            });
            camera.startPreview();
        } catch (Exception error) { Toast.makeText(this, "相机无法使用，请返回并使用 PDA 扫码键。", Toast.LENGTH_LONG).show(); finish(); }
    }
    @Override public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) { }
    @Override public void surfaceDestroyed(SurfaceHolder holder) { release(); }
    private void release() { if (camera != null) { camera.setPreviewCallback(null); camera.stopPreview(); camera.release(); camera = null; } }
    @Override protected void onPause() { super.onPause(); release(); }
    @Override protected void onDestroy() { finished = true; decoder.shutdown(); super.onDestroy(); }
}
