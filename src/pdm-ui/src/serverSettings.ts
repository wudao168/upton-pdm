import { installButtonTypography } from './buttonTypography'
import { installPanelHeaderActions } from './panelHeaderActions'
import { installColumnResize } from './columnResize'
import './styles/column-resize.css'
import { createApp } from 'vue'
import ElementPlus from 'element-plus'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import 'element-plus/dist/index.css'
import './styles/tokens.css'
import './styles/app.css'
import './styles/ui-consistency.css'
import ServerConnectionSettings from './components/ServerConnectionSettings.vue'

const app = createApp(ServerConnectionSettings).use(ElementPlus, { locale: zhCn })
installPanelHeaderActions(app)
installColumnResize(app)
installButtonTypography(app)
app.mount('#app')
