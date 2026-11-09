<?xml version="1.0" encoding="utf-8"?>
<xsl:stylesheet version="1.0"
    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
    xmlns:svg="http://www.w3.org/2000/svg"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns="https://github.com/avaloniaui"
    exclude-result-prefixes="svg">
  <xsl:import href="SvgPathGeometry.xslt" />
  <xsl:output method="xml" encoding="utf-8" indent="yes" />
  <xsl:key name="gradient" match="svg:linearGradient" use="@id" />

  <xsl:template match="/svg:svg">
    <ResourceDictionary>
      <DrawingImage x:Key="AppIcon" Viewbox="{@viewBox}">
        <DrawingGroup>
          <xsl:apply-templates select="*" mode="drawing" />
        </DrawingGroup>
      </DrawingImage>
    </ResourceDictionary>
  </xsl:template>

  <xsl:template match="svg:defs | svg:title | svg:desc" mode="drawing" />

  <xsl:template match="svg:g" mode="drawing">
    <xsl:apply-templates select="*" mode="drawing" />
  </xsl:template>

  <xsl:template match="svg:path | svg:circle | svg:rect" mode="drawing">
    <GeometryDrawing>
      <xsl:attribute name="Geometry">
        <xsl:apply-templates select="." mode="geometry" />
      </xsl:attribute>
      <xsl:choose>
        <xsl:when test="starts-with(@fill, 'url(#')">
          <xsl:variable name="gradient" select="key('gradient', substring-before(substring-after(@fill, '#'), ')'))" />
          <xsl:if test="not($gradient) or ($gradient/@gradientUnits = 'userSpaceOnUse' and not(self::svg:rect))">
            <xsl:message terminate="yes">Unsupported gradient in the app icon SVG.</xsl:message>
          </xsl:if>
          <GeometryDrawing.Brush>
            <LinearGradientBrush>
              <xsl:attribute name="StartPoint">
                <xsl:call-template name="gradient-point">
                  <xsl:with-param name="gradient" select="$gradient" />
                  <xsl:with-param name="x" select="$gradient/@x1" />
                  <xsl:with-param name="y" select="$gradient/@y1" />
                </xsl:call-template>
              </xsl:attribute>
              <xsl:attribute name="EndPoint">
                <xsl:call-template name="gradient-point">
                  <xsl:with-param name="gradient" select="$gradient" />
                  <xsl:with-param name="x" select="$gradient/@x2" />
                  <xsl:with-param name="y" select="$gradient/@y2" />
                  <xsl:with-param name="default-x" select="1" />
                </xsl:call-template>
              </xsl:attribute>
              <xsl:for-each select="$gradient/svg:stop">
                <GradientStop Offset="{@offset}" Color="{@stop-color}" />
              </xsl:for-each>
            </LinearGradientBrush>
          </GeometryDrawing.Brush>
        </xsl:when>
        <xsl:when test="@fill != 'none'">
          <xsl:attribute name="Brush"><xsl:value-of select="@fill" /></xsl:attribute>
        </xsl:when>
      </xsl:choose>
      <xsl:if test="@stroke and @stroke != 'none'">
        <GeometryDrawing.Pen>
          <Pen Brush="{@stroke}" Thickness="{@stroke-width}" LineCap="Flat" LineJoin="Miter" />
        </GeometryDrawing.Pen>
      </xsl:if>
    </GeometryDrawing>
  </xsl:template>

  <xsl:template match="*" mode="drawing" priority="-1">
    <xsl:message terminate="yes">Unsupported element in the app icon SVG: <xsl:value-of select="local-name()" />.</xsl:message>
  </xsl:template>

  <!-- Avalonia's relative brush coordinates are measured from the geometry bounds. -->
  <xsl:template name="gradient-point">
    <xsl:param name="gradient" />
    <xsl:param name="x" />
    <xsl:param name="y" />
    <xsl:param name="default-x" select="0" />
    <xsl:choose>
      <xsl:when test="$gradient/@gradientUnits = 'userSpaceOnUse'">
        <xsl:value-of select="concat(100 * ($x - @x) div @width, '%,', 100 * ($y - @y) div @height, '%')" />
      </xsl:when>
      <xsl:otherwise>
        <xsl:value-of select="100 * (sum($x) + $default-x * not($x))" /><xsl:text>%,</xsl:text>
        <xsl:value-of select="100 * sum($y)" /><xsl:text>%</xsl:text>
      </xsl:otherwise>
    </xsl:choose>
  </xsl:template>
</xsl:stylesheet>
