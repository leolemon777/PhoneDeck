package androidx.core.app;

import android.app.Activity;

/** 见 androidx.core.content.ContextCompat：仅为扫码库转发系统权限请求。 */
public final class ActivityCompat {
    private ActivityCompat() {
    }

    public static void requestPermissions(Activity activity, String[] permissions, int requestCode) {
        activity.requestPermissions(permissions, requestCode);
    }
}
