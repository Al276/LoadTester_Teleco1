# LoadTester - Web API Resilience Analysis

## Descripción
Aplicación de consola desarrollada en .NET diseñada para ejecutar pruebas de carga controladas contra endpoints HTTP. Este proyecto permite evaluar el comportamiento, la degradación de rendimiento y la resiliencia de una Web API bajo diferentes niveles de tráfico. 

## Características Principales
* **Configuración dinámica:** Permite parametrizar la URL del endpoint objetivo para evaluar diferentes rutas].
* **Simulación de carga:** Generación de peticiones HTTP repetitivas variando parámetros como la concurrencia y la frecuenci.
* **Recolección de métricas:** Registro de peticiones totales, conteo de respuestas exitosas y fallidas.
* **Monitoreo de recursos:** Observación de los tiempos de respuesta y evaluación del impacto en la CPU y la memoria de la máquina local durante la prueb.

## Contexto Académico
Este proyecto fue desarrollado como parte del laboratorio "Controlled Load Testing and Resilience Analysis of a Web API" para el programa de Ingeniería de Sistemas (2026-1) de la Universidad de los Llanos 
* **Autores:** Alex,, Brayan,  Eduar, Niccolás
* **Objetivo de prueba:** Identificar el punto de degradación de la API en el repositorio `mini-identity-api-dotnet` operando en un entorno local.

## Restricciones y Uso Ético
Esta herramienta fue creada estrictamente para experimentación académica de rendimiento y escalabilidad.
* Las pruebas solo deben ejecutarse en la máquina locall (localhost) o en un entorno privado controlado.
* Queda prohibido dirigir esta aplicación contra servicios públicos, infraestructura de terceros, la nube o sistemas de la universidad.
* La ejecución debe detenerse inmediatamente si la máquinaa local presenta inestabilidad severa.
