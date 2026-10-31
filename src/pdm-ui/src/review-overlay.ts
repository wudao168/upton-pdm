import { installGlobalScrollbars } from './globalScrollbars'
import { installContainerBorders } from './containerBorders'
import { installButtonTypography } from './buttonTypography'
import { installButtonStyles } from './buttonStyles'
import { installPanelHeaderActions } from './panelHeaderActions'
import { installColumnResize } from './columnResize'
import { installDatePickers } from './datePickers'
import './styles/column-resize.css'
import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/themes.css'
import './styles/app.css'
import './styles/ui-consistency.css'
import './styles/tabs.css'
import './styles/review-overlay.css'
import DrawingReviewOverlayApp from './components/DrawingReviewOverlayApp.vue'
import { installClientDiagnostics } from './clientDiagnostics'

const app = createApp(DrawingReviewOverlayApp).use(ElementPlus, { locale: zhCn })
installClientDiagnostics(app)
installPanelHeaderActions(app)
installColumnResize(app)
installDatePickers(app)
installButtonTypography(app)
installButtonStyles(app)
installGlobalScrollbars(app)
app.onUnmount(installContainerBorders())
app.mount('#review-overlay-root')
