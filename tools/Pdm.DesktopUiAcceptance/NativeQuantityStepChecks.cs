using System;
using System.Collections.Generic;
using System.Threading.Tasks;

internal static partial class Program
{
    private static async Task<object> CheckNativeQuantityStep(object core)
    {
        const string id = "pdm-qa-quantity-step";
        var values = new Dictionary<string, string>();
        await Execute(core, "(()=>{window.__quantityQaFocus=document.activeElement;const s=document.createElement('style');s.id='" + id + "-style';s.textContent='#" + id + "::-webkit-inner-spin-button{opacity:1}';document.head.append(s);const i=document.createElement('input');i.id='" + id + "';i.type='number';i.min='0.0001';i.max='5';i.step='any';i.value='1.25';i.style.cssText='position:fixed;left:16px;top:16px;width:180px;height:32px;box-sizing:border-box;z-index:2147483647';document.body.append(i);i.focus()})()");
        try
        {
            foreach (var key in new[] { "ArrowUp", "ArrowDown" })
            {
                var code = key == "ArrowUp" ? 38 : 40;
                await NativeProtocol(core, "Input.dispatchKeyEvent", new { type = "keyDown", key, windowsVirtualKeyCode = code });
                await NativeProtocol(core, "Input.dispatchKeyEvent", new { type = "keyUp", key, windowsVirtualKeyCode = code });
                values[key] = Json.Deserialize<string>(await Execute(core, "document.getElementById('" + id + "').value"));
            }
            if (values["ArrowUp"] != "2.25" || values["ArrowDown"] != "1.25") throw new Exception("桌面数量上下键未按1增减。");
            var box = Json.Deserialize<Dictionary<string, object>>(Json.Deserialize<string>(await Execute(core,
                "JSON.stringify((()=>{const i=document.getElementById('" + id + "'),r=i.getBoundingClientRect(),s=getComputedStyle(i);return{x:r.x,y:r.y,width:r.width,height:r.height,paddingRight:parseFloat(s.paddingRight)||0,borderRight:parseFloat(s.borderRightWidth)||0}})())")));
            // Chromium places the native spinner inside the content box, before the capsule padding.
            var x = Convert.ToDouble(box["x"]) + Convert.ToDouble(box["width"])
                - Convert.ToDouble(box["paddingRight"]) - Convert.ToDouble(box["borderRight"]) - 8;
            foreach (var delta in new[] { -4, 4 })
            {
                var y = Convert.ToDouble(box["y"]) + Convert.ToDouble(box["height"]) / 2 + delta;
                await NativeProtocol(core, "Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
                await NativeProtocol(core, "Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 });
                await NativeProtocol(core, "Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 });
                values[delta < 0 ? "buttonUp" : "buttonDown"] = Json.Deserialize<string>(await Execute(core, "document.getElementById('" + id + "').value"));
            }
            if (values["buttonUp"] != "2.25" || values["buttonDown"] != "1.25") throw new Exception("桌面数量按钮未按1增减，实际结果：" + Json.Serialize(values));
            if (await Execute(core, "document.getElementById('" + id + "').checkValidity()") != "true") throw new Exception("桌面数量小数校验发生变化。");
            Console.WriteLine("PASS native quantity arrows and keys: 1.25 ↔ 2.25; fractional validity retained");
            return values;
        }
        finally
        {
            await Execute(core, "document.getElementById('" + id + "')?.remove();document.getElementById('" + id + "-style')?.remove();window.__quantityQaFocus?.focus();delete window.__quantityQaFocus");
        }
    }

    private static async Task NativeProtocol(object core, string method, object parameters)
    {
        var task = (Task<string>)core.GetType().GetMethod("CallDevToolsProtocolMethodAsync").Invoke(core, new object[] { method, Json.Serialize(parameters) });
        if (await Task.WhenAny(task, Task.Delay(10000)) != task) throw new TimeoutException("桌面数量操作校验超时。");
        await task;
    }
}
