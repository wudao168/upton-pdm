import { nextTick, onBeforeUnmount, onMounted, watch, type Ref } from 'vue'

/** 只改变图形呈现，不插值业务数值；离页、离屏和销毁时取消所有动画。 */
export function useReportChartMotion(host:Ref<HTMLElement|undefined>,active:()=>boolean,baseline:()=>number,source:()=>unknown){
 let observer:IntersectionObserver|undefined,media:MediaQueryList|undefined,visible=false,frame=0,generation=0
 let animations:Animation[]=[]
 const stop=()=>{generation++;cancelAnimationFrame(frame);animations.forEach(a=>a.cancel());animations=[];if(host.value)host.value.dataset.motionState='settled'}
 const animate=(element:SVGElement,frames:Keyframe[],duration:number,delay=0,easing='cubic-bezier(.22,1,.36,1)')=>{
  const animation=element.animate(frames,{duration,delay,easing,fill:'backwards'});animation.id='report-chart-motion';animations.push(animation)
 }
 function play(){
  const root=host.value;if(!root||!visible||!active()||media?.matches)return
  const run=generation;root.dataset.motionState='running'
  root.querySelectorAll<SVGElement>('[data-chart-motion="bar"]').forEach((el,i)=>{
   el.style.transformBox='view-box';el.style.transformOrigin='0px '+baseline()+'px'
   animate(el,[{transform:'scaleY(0)',opacity:.35},{transform:'scaleY(1)',opacity:1}],680,Math.min(i,7)*45)
  })
  root.querySelectorAll<SVGPathElement>('[data-chart-motion="line"]').forEach((el,i)=>{
   const length=el.getTotalLength();if(!length)return
   animate(el,[{strokeDasharray:length+' '+length,strokeDashoffset:length},{strokeDasharray:length+' '+length,strokeDashoffset:0}],900,Math.min(i,3)*65)
  })
  root.querySelectorAll<SVGElement>('[data-chart-motion="arc"]').forEach(el=>{
   const target=el.getAttribute('stroke-dasharray')||'',parts=target.split(/\s+/).map(Number),length=parts[0]||0,whole=parts[1]||1
   if(length<=0)return
   const delay=Math.abs(Number(el.getAttribute('stroke-dashoffset')||0))/whole*850
   animate(el,[{strokeDasharray:'0 '+whole},{strokeDasharray:target}],Math.max(120,length/whole*850),delay,'cubic-bezier(.25,.46,.45,.94)')
  })
  root.querySelectorAll<SVGElement>('[data-chart-motion="area"]').forEach(el=>{
   el.style.transformBox='view-box';el.style.transformOrigin='0px '+baseline()+'px'
   animate(el,[{opacity:0,transform:'scaleY(.08)'},{opacity:1,transform:'scaleY(1)'}],900,80)
  })
  root.querySelectorAll<SVGElement>('[data-chart-motion="point"]').forEach((el,i)=>{
   el.style.transformBox='fill-box';el.style.transformOrigin='center'
   animate(el,[{opacity:0,transform:'scale(.3)'},{opacity:1,transform:'scale(1)'}],440,100+Math.min(i,15)*35)
  })
  root.querySelectorAll<SVGElement>('[data-chart-motion="label"]').forEach(el=>animate(el,[{opacity:0},{opacity:1}],300,300))
  const running=animations.slice()
  void Promise.allSettled(running.map(a=>a.finished)).then(()=>{if(generation===run){root.dataset.motionState='settled';running.forEach(a=>a.cancel());animations=[]}})
 }
 function queue(){stop();if(!visible||!active()||media?.matches)return;const run=generation;void nextTick(()=>{if(run===generation)frame=requestAnimationFrame(play)})}
 onMounted(()=>{
  media=window.matchMedia('(prefers-reduced-motion: reduce)');media.addEventListener('change',queue)
  observer=new IntersectionObserver(entries=>{const next=entries.some(e=>e.isIntersecting);if(next===visible)return;visible=next;if(visible)queue();else stop()},{threshold:0})
  if(host.value)observer.observe(host.value)
 })
 watch(()=>[active(),source()],queue,{deep:true,flush:'post'})
 onBeforeUnmount(()=>{stop();observer?.disconnect();media?.removeEventListener('change',queue)})
}
