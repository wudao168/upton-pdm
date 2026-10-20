import { installGlobalScrollbars } from './globalScrollbars'
import { installButtonTypography } from './buttonTypography'
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
import ServerConnectionSettings from './components/ServerConnectionSettings.vue'
import { isPdmTheme } from './themes'

const savedTheme = window.localStorage.getItem('pdm_theme')
document.documentElement.dataset.pdmTheme = isPdmTheme(savedTheme) ? savedTheme : 'a'

const app = createApp(ServerConnectionSettings).use(ElementPlus, { locale: zhCn })
installPanelHeaderActions(app)
installColumnResize(app)
installDatePickers(app)
installButtonTypography(app)
installGlobalScrollbars(app)
app.mount('#app')
