package de.sunmiprint.bridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.ComponentName;
import android.content.Intent;
import android.content.ServiceConnection;
import android.graphics.Bitmap;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.RemoteException;
import android.os.SystemClock;
import android.util.Log;
import com.sunmi.peripheral.printer.InnerResultCallback;
import com.sunmi.peripheral.printer.SunmiPrinterService;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;

public final class PrintBridge extends Service {
    private static final String SOCKET = "de.sunmiprint.bridge.v1";
    private static final String PRINTER = "woyou.aidlservice.jiuiv5";
    private static final int MAGIC = 0x534e5031; // SNP1
    private static final int WIDTH = 384;
    private static final int MAX_HEIGHT = 8192;
    private static final int STRIP_HEIGHT = 256;
    private static final long IDLE_MS = 90_000;
    private final Handler handler = new Handler();
    private final Object printerReady = new Object();
    private volatile SunmiPrinterService printer;
    private volatile long lastRequest;
    private volatile boolean handlingRequest;
    private LocalServerSocket server;
    private boolean bound;

    private final Runnable stopWhenIdle = new Runnable() {
        @Override public void run() {
            if (!handlingRequest && SystemClock.elapsedRealtime() - lastRequest >= IDLE_MS) {
                stopSelf();
            } else {
                handler.postDelayed(this, IDLE_MS / 3);
            }
        }
    };

    private final ServiceConnection printerConnection = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name, IBinder binder) {
            synchronized (printerReady) {
                printer = SunmiPrinterService.Stub.asInterface(binder);
                printerReady.notifyAll();
            }
        }

        @Override public void onServiceDisconnected(ComponentName name) {
            printer = null;
        }
    };

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        lastRequest = SystemClock.elapsedRealtime();
        if (server != null) {
            return START_NOT_STICKY;
        }

        Notification.Builder notification;
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel channel = new NotificationChannel(
                    "printing", "Windows-Druck", NotificationManager.IMPORTANCE_LOW);
            getSystemService(NotificationManager.class).createNotificationChannel(channel);
            notification = new Notification.Builder(this, "printing");
        } else {
            notification = new Notification.Builder(this);
        }
        startForeground(1, notification.setSmallIcon(android.R.drawable.ic_menu_send)
                .setContentTitle("SUNMI Print")
                .setContentText("Bereit für Druckaufträge vom Windows-PC")
                .setOngoing(true).build());
        try {
            server = new LocalServerSocket(SOCKET);
        } catch (IOException error) {
            Log.e("SunmiPrint", "Could not open local ADB socket", error);
            stopSelf();
            return START_NOT_STICKY;
        }
        bound = bindService(new Intent(PRINTER + ".IWoyouService").setPackage(PRINTER),
                printerConnection, BIND_AUTO_CREATE);
        LocalServerSocket listener = server;
        new Thread(() -> serve(listener), "sunmi-print-bridge").start();
        handler.postDelayed(stopWhenIdle, IDLE_MS);
        return START_NOT_STICKY;
    }

    @Override public IBinder onBind(Intent intent) {
        return null;
    }

    @Override public void onDestroy() {
        handler.removeCallbacks(stopWhenIdle);
        if (server != null) {
            try {
                server.close();
            } catch (IOException error) {
                Log.w("SunmiPrint", "Could not close local ADB socket", error);
            }
        }
        if (bound) {
            unbindService(printerConnection);
        }
        super.onDestroy();
    }

    private void serve(LocalServerSocket listener) {
        while (true) {
            LocalSocket accepted;
            try {
                accepted = listener.accept();
            } catch (IOException closed) {
                return;
            }
            try (LocalSocket socket = accepted) {
                int uid = socket.getPeerCredentials().getUid();
                if (uid != 2000 && uid != 0) {
                    continue;
                }
                handlingRequest = true;
                socket.setSoTimeout(5000);
                handle(socket);
            } catch (IOException error) {
                Log.d("SunmiPrint", "ADB connection ended", error);
            } finally {
                if (handlingRequest) {
                    lastRequest = SystemClock.elapsedRealtime();
                    handlingRequest = false;
                }
            }
        }
    }

    private void handle(LocalSocket socket) throws IOException {
        DataInputStream input = new DataInputStream(socket.getInputStream());
        OutputStream output = socket.getOutputStream();
        if (input.readInt() != MAGIC) {
            reply(output, "ERR", "Diese Version der Druckbrücke ist inkompatibel.");
            return;
        }
        int command = input.readUnsignedByte();
        try {
            SunmiPrinterService device = waitForPrinter();
            if (device == null) {
                reply(output, "ERR", "Der SUNMI-Druckdienst ist nicht verfügbar.");
                return;
            }
            int status = device.updatePrinterState();
            if (command == 'Q') {
                reply(output, status == 1 ? "OK" : "STATUS", describe(status));
            } else if (command == 'P') {
                int height = input.readUnsignedShort();
                if (height < 1 || height > MAX_HEIGHT) {
                    reply(output, "ERR", "Die Beleglänge liegt außerhalb des Druckbereichs.");
                    return;
                }
                byte[] raster = new byte[(WIDTH / 8) * height];
                input.readFully(raster);
                if (status != 1) {
                    reply(output, "ERR", describe(status));
                    return;
                }
                String failure = print(device, raster, height);
                reply(output, failure == null ? "OK" : "ERR",
                        failure == null ? "Der SUNMI hat den Druck bestätigt." : failure);
            } else {
                reply(output, "ERR", "Unbekannter Druckbefehl.");
            }
        } catch (RemoteException error) {
            Log.e("SunmiPrint", "Printer service call failed", error);
            reply(output, "ERR", "Die Rückmeldung des SUNMI-Druckdienstes ist ausgefallen.");
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            reply(output, "ERR", "Der Druckdienst wurde unterbrochen.");
        }
    }

    private SunmiPrinterService waitForPrinter() throws InterruptedException {
        long deadline = SystemClock.elapsedRealtime() + 4000;
        synchronized (printerReady) {
            while (printer == null && bound) {
                long remaining = deadline - SystemClock.elapsedRealtime();
                if (remaining <= 0) {
                    break;
                }
                printerReady.wait(remaining);
            }
            return printer;
        }
    }

    private String print(SunmiPrinterService device, byte[] raster, int height)
            throws RemoteException, InterruptedException {
        CountDownLatch done = new CountDownLatch(1);
        AtomicReference<String> result = new AtomicReference<>();
        InnerResultCallback completion = new InnerResultCallback() {
            @Override public void onPrintResult(int code, String message) {
                result.set(code == 0 ? "OK" : "FEHLER");
                done.countDown();
            }

            @Override public void onRaiseException(int code, String message) {
                result.set("FEHLER");
                done.countDown();
            }

            @Override public void onRunResult(boolean success) {
                if (!success) {
                    result.set("FEHLER");
                    done.countDown();
                }
            }

            @Override public void onReturnString(String value) {
            }
        };

        device.enterPrinterBuffer(true);
        boolean submitted = false;
        try {
            for (int top = 0; top < height; top += STRIP_HEIGHT) {
                int rows = Math.min(STRIP_HEIGHT, height - top);
                int[] pixels = new int[WIDTH * rows];
                for (int index = 0; index < pixels.length; index++) {
                    int bitIndex = top * WIDTH + index;
                    boolean black = (raster[bitIndex / 8] & (0x80 >>> (bitIndex % 8))) != 0;
                    pixels[index] = black ? 0xff000000 : 0xffffffff;
                }
                Bitmap strip = Bitmap.createBitmap(pixels, WIDTH, rows, Bitmap.Config.ARGB_8888);
                try {
                    device.printBitmap(strip, null);
                } finally {
                    strip.recycle();
                }
            }
            device.lineWrap(3, null);
            device.exitPrinterBufferWithCallback(true, completion);
            submitted = true;
        } finally {
            if (!submitted) {
                device.exitPrinterBuffer(false);
            }
        }
        if (!done.await(75, TimeUnit.SECONDS)) {
            return "Keine Druckbestätigung erhalten. Prüfe den Beleg vor einem erneuten Versuch.";
        }
        if ("OK".equals(result.get())) {
            return null;
        }
        int status = device.updatePrinterState();
        return status == 1 ? "Der SUNMI meldet einen fehlgeschlagenen Druck." : describe(status);
    }

    private static String describe(int state) {
        switch (state) {
            case 1: return "Drucker bereit";
            case 2: return "Drucker startet";
            case 3: return "Kommunikationsfehler am Drucker";
            case 4: return "Papier fehlt. Lege eine Bonrolle ein.";
            case 5: return "Drucker ist zu heiß. Lass ihn abkühlen.";
            case 6: return "Druckerabdeckung ist offen.";
            case 505: return "Interner Drucker nicht gefunden.";
            default: return "Drucker meldet Status " + state + ".";
        }
    }

    private static void reply(OutputStream output, String state, String message) throws IOException {
        output.write((state + "|" + message + "\n").getBytes(StandardCharsets.UTF_8));
        output.flush();
    }
}
