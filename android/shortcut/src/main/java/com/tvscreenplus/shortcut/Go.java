package com.tvscreenplus.shortcut;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;

/** Opens the TVScreenPlus that made this shortcut (package "<its package>.sc.x…"), which connects and launches the game. */
public class Go extends Activity {
    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        String me = getPackageName();
        int at = me.lastIndexOf(".sc.");
        if (at > 0) try {
            startActivity(new Intent("com.tvscreenplus.SHORTCUT")
                .setClassName(me.substring(0, at), "com.tvscreenplus.MainActivity")
                .putExtra("shortcut", me)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));
        } catch (Exception ignored) { } // TVScreenPlus uninstalled: nothing to open
        finish();
    }
}
